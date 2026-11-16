#define UNICODE
#define _UNICODE
#include <windows.h>
#include <shlobj.h>
#include <sddl.h>
#include <bcrypt.h>
#include <filesystem>
#include <fstream>
#include <sstream>
#include <vector>
#include <stdexcept>
#include <string>
#include <functional>
#include <array>
#pragma comment(lib, "bcrypt.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "ole32.lib")
namespace fs = std::filesystem;

struct Handle {
    HANDLE value;
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
};

static std::wstring Wide(const std::string& text) {
    int length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), (int)text.size(), nullptr, 0);
    if (!length) throw std::runtime_error("Invalid UTF-8 in runtime manifest.");
    std::wstring result(length, L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), (int)text.size(), result.data(), length);
    return result;
}

static fs::path Relative(const std::string& text) {
    fs::path path(Wide(text));
    if (path.empty() || path.has_root_path() || text.find(':') != std::string::npos)
        throw std::runtime_error("Absolute paths are not allowed in the manifest.");
    for (const auto& part : path)
        if (part == L"..") throw std::runtime_error("Path traversal in the runtime manifest.");
    return path;
}

class RuntimeHasher {
public:
    RuntimeHasher() {
        if (BCryptCreateHash(BCRYPT_SHA256_ALG_HANDLE, &hash_, nullptr, 0, nullptr, 0, BCRYPT_HASH_REUSABLE_FLAG) < 0)
            throw std::runtime_error("Cannot create a reusable SHA-256 hash.");
    }

    RuntimeHasher(const RuntimeHasher&) = delete;
    RuntimeHasher& operator=(const RuntimeHasher&) = delete;
    ~RuntimeHasher() { BCryptDestroyHash(hash_); }

    std::string Sha256(const fs::path& path) {
        Handle input{CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
            FILE_FLAG_SEQUENTIAL_SCAN, nullptr)};
        if (input.value == INVALID_HANDLE_VALUE)
            throw std::runtime_error("Cannot open a runtime file for integrity validation.");

        DWORD read{};
        do {
            if (!ReadFile(input.value, buffer_.data(), (DWORD)buffer_.size(), &read, nullptr))
                throw std::runtime_error("Cannot read a runtime file for integrity validation.");
            if (read && BCryptHashData(hash_, buffer_.data(), read, 0) < 0)
                throw std::runtime_error("Cannot hash a runtime file.");
        } while (read);

        std::array<UCHAR, 32> digest{};
        if (BCryptFinishHash(hash_, digest.data(), (ULONG)digest.size(), 0) < 0)
            throw std::runtime_error("Cannot finish SHA-256 hash.");
        constexpr char alphabet[] = "0123456789abcdef";
        std::string result;
        result.reserve(digest.size() * 2);
        for (auto byte : digest) { result += alphabet[byte >> 4]; result += alphabet[byte & 15]; }
        return result;
    }

private:
    BCRYPT_HASH_HANDLE hash_{};
    std::array<UCHAR, 65536> buffer_;
};

// Restrict executable caches to administrators/System. Regular users can read them.
static void SecureDirectory(const fs::path& path) {
    PSECURITY_DESCRIPTOR descriptor{};
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
        L"D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;GRGX;;;BU)",
        SDDL_REVISION_1, &descriptor, nullptr)) throw std::runtime_error("Cannot create cache ACL.");
    SECURITY_ATTRIBUTES attributes{sizeof(attributes), descriptor, FALSE};
    bool made = CreateDirectoryW(path.c_str(), &attributes) != FALSE;
    auto error = GetLastError();
    // Apply the protected ACL to pre-existing application cache directories, too.
    bool secured = SetFileSecurityW(path.c_str(), DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION,
        descriptor) != FALSE;
    LocalFree(descriptor);
    if ((!made && error != ERROR_ALREADY_EXISTS) || !secured)
        throw std::runtime_error("Cannot create the protected runtime cache; run as administrator.");
}

static fs::path Prepare(const fs::path& root) {
    auto manifest = root / L"Runtime" / L"layout.tsv";
    RuntimeHasher hasher;
    auto identity = hasher.Sha256(manifest);
    PWSTR common{};
    if (FAILED(SHGetKnownFolderPath(FOLDERID_ProgramData, 0, nullptr, &common)))
        throw std::runtime_error("Cannot locate ProgramData.");
    fs::path cacheRoot(common);
    CoTaskMemFree(common);
    cacheRoot /= L"GProControl";
    SecureDirectory(cacheRoot);
    cacheRoot /= L"Runtime";
    SecureDirectory(cacheRoot);
    auto cache = cacheRoot / Wide(identity);
    SecureDirectory(cache);
    auto mutexName = L"Local\\GProControl.Runtime." + Wide(identity);
    Handle mutex{CreateMutexW(nullptr, FALSE, mutexName.c_str())};
    if (!mutex.value) throw std::runtime_error("Cannot lock runtime preparation.");
    auto wait = WaitForSingleObject(mutex.value, 60000);
    if (wait != WAIT_OBJECT_0 && wait != WAIT_ABANDONED)
        throw std::runtime_error("Runtime preparation is busy; try again.");
    try {
        std::ifstream input(manifest, std::ios::binary);
        std::string line;
        size_t count{};
        while (std::getline(input, line)) {
            if (!line.empty() && line.back() == '\r') line.pop_back();
            if (line.empty() || line.front() == '#') continue;
            std::istringstream row(line);
            std::string destinationText, sourceText, expectedHash;
            if (!std::getline(row, destinationText, '\t') || !std::getline(row, sourceText, '\t')
                || !std::getline(row, expectedHash) || expectedHash.size() != 64)
                throw std::runtime_error("Invalid runtime manifest entry.");
            auto source = root / Relative(sourceText);
            auto destination = cache / Relative(destinationText);
            if (fs::exists(destination) && hasher.Sha256(destination) == expectedHash) { ++count; continue; }
            if (hasher.Sha256(source) != expectedHash) throw std::runtime_error("An installed runtime file is corrupted; reinstall G Pro Control.");
            fs::create_directories(destination.parent_path());
            if (fs::exists(destination)) fs::remove(destination);
            // Copy, rather than hard-link: an upgrade can safely replace installed files while an old cache runs.
            fs::copy_file(source, destination);
            if (hasher.Sha256(destination) != expectedHash) throw std::runtime_error("Runtime cache integrity validation failed.");
            ++count;
        }
        if (!count || !fs::exists(cache / L"GProControl.exe"))
            throw std::runtime_error("The runtime manifest does not contain the application.");
        ReleaseMutex(mutex.value);
    } catch (...) { ReleaseMutex(mutex.value); throw; }
    return cache;
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR arguments, int) {
    try {
        std::array<wchar_t, 32768> executable{};
        if (!GetModuleFileNameW(nullptr, executable.data(), (DWORD)executable.size()))
            throw std::runtime_error("Cannot locate launcher.");
        auto root = fs::path(executable.data()).parent_path();
        auto runtime = Prepare(root);
        if (std::wstring(arguments) == L"--prepare-runtime") return 0;
        const bool smoke = std::wstring(arguments).find(L"--package-smoke") != std::wstring::npos;
        const auto mutexName = smoke ? L"Local\\GProControl.Smoke." + std::to_wstring(std::hash<std::wstring>{}(root.wstring()))
            : std::wstring(L"Local\\GProControl.InstalledApp");
        Handle instance{CreateMutexW(nullptr, TRUE, mutexName.c_str())};
        if (!instance.value) throw std::runtime_error("Cannot create the application mutex.");
        if (GetLastError() == ERROR_ALREADY_EXISTS) {
            if (!smoke) PostMessageW(HWND_BROADCAST, RegisterWindowMessageW(L"GProControl.ShowMainWindow"), 0, 0);
            return 0;
        }
        SetEnvironmentVariableW(L"GPROCONTROL_LAUNCHER_PATH", executable.data());
        auto child = runtime / L"GProControl.exe";
        std::wstring command = L"\"" + child.wstring() + L"\" " + arguments;
        STARTUPINFOW startup{sizeof(startup)};
        PROCESS_INFORMATION process{};
        if (!CreateProcessW(child.c_str(), command.data(), nullptr, nullptr, FALSE, 0,
            nullptr, runtime.c_str(), &startup, &process))
            throw std::runtime_error("Cannot start G Pro Control from its runtime cache.");
        Handle processHandle{process.hProcess}, threadHandle{process.hThread};
        WaitForSingleObject(processHandle.value, INFINITE);
        DWORD exitCode{};
        GetExitCodeProcess(processHandle.value, &exitCode);
        return (int)exitCode;
    } catch (const std::exception& error) {
        if (std::wstring(arguments) == L"--prepare-runtime") {
            OutputDebugStringA(error.what());
            return 1;
        }
        auto message = L"G Pro Control 启动失败\n\n" + Wide(error.what());
        MessageBoxW(nullptr, message.c_str(), L"G Pro Control", MB_OK | MB_ICONERROR);
        return 1;
    }
}
