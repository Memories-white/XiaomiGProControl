#include <windows.h>
#include <shlobj.h>
#include <sddl.h>
#include <string>
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "user32.lib")

// The embedded Inno loader extracts before elevating. Elevate this outer EXE
// through its manifest instead, before creating any temporary files.
int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR arguments, int) {
    std::wstring directory, payload;
    HANDLE file = INVALID_HANDLE_VALUE;
    DWORD result = ERROR_INSTALL_FAILURE;
    try {
        PWSTR programData = nullptr;
        if (FAILED(SHGetKnownFolderPath(FOLDERID_ProgramData, 0, nullptr, &programData)))
            throw L"无法获取系统安装临时目录。";
        directory = programData;
        CoTaskMemFree(programData);
        GUID id{};
        if (FAILED(CoCreateGuid(&id))) throw L"无法生成安装标识。";
        wchar_t text[40]{};
        StringFromGUID2(id, text, 40);
        directory += L"\\GProControl-Setup-";
        directory += text;
        PSECURITY_DESCRIPTOR descriptor = nullptr;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
            L"D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", SDDL_REVISION_1, &descriptor, nullptr))
            throw L"无法准备安装目录权限。";
        SECURITY_ATTRIBUTES security{sizeof(SECURITY_ATTRIBUTES), descriptor, FALSE};
        const bool created = CreateDirectoryW(directory.c_str(), &security) != FALSE;
        LocalFree(descriptor);
        if (!created) { directory.clear(); throw L"无法创建受保护的安装临时目录。"; }
        payload = directory + L"\\Setup.exe";
        const HRSRC resource = FindResourceW(instance, MAKEINTRESOURCEW(101), MAKEINTRESOURCEW(10));
        const DWORD size = resource ? SizeofResource(instance, resource) : 0;
        const HGLOBAL loaded = resource ? LoadResource(instance, resource) : nullptr;
        const void* data = loaded ? LockResource(loaded) : nullptr;
        if (!data || !size) throw L"安装包资源缺失，请重新下载安装包。";
        file = CreateFileW(payload.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW,
            FILE_ATTRIBUTE_NORMAL, nullptr);
        DWORD written = 0;
        if (file == INVALID_HANDLE_VALUE || !WriteFile(file, data, size, &written, nullptr) || written != size)
            throw L"无法准备安装程序，请检查磁盘空间。";
        CloseHandle(file); file = INVALID_HANDLE_VALUE;
        std::wstring command = L"\"" + payload + L"\" ";
        command += arguments;
        STARTUPINFOW startup{sizeof(STARTUPINFOW)};
        PROCESS_INFORMATION process{};
        if (!CreateProcessW(payload.c_str(), command.data(), nullptr, nullptr, FALSE,
            0, nullptr, directory.c_str(), &startup, &process))
            throw L"无法启动安装向导。";
        CloseHandle(process.hThread);
        WaitForSingleObject(process.hProcess, INFINITE);
        GetExitCodeProcess(process.hProcess, &result);
        CloseHandle(process.hProcess);
    } catch (const wchar_t* message) {
        MessageBoxW(nullptr, message, L"G Pro Control 安装", MB_OK | MB_ICONERROR);
    } catch (...) {
        MessageBoxW(nullptr, L"无法启动安装程序。", L"G Pro Control 安装", MB_OK | MB_ICONERROR);
    }
    if (file != INVALID_HANDLE_VALUE) CloseHandle(file);
    if (!payload.empty()) DeleteFileW(payload.c_str());
    if (!directory.empty()) RemoveDirectoryW(directory.c_str());
    return static_cast<int>(result);
}
