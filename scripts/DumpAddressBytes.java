// Dump bytes and best-effort ASCII strings at selected virtual addresses.
// @category XiaomiG

import java.io.File;
import java.io.PrintWriter;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;

public class DumpAddressBytes extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 2) {
            throw new IllegalArgumentException("Expected output file followed by addresses");
        }
        File output = new File(args[0]).getAbsoluteFile();
        output.getParentFile().mkdirs();
        try (PrintWriter writer = new PrintWriter(output, "UTF-8")) {
            for (int i = 1; i < args.length; i++) {
                Address address = currentProgram.getAddressFactory().getDefaultAddressSpace().getAddress(args[i]);
                byte[] bytes = new byte[96];
                int count = currentProgram.getMemory().getBytes(address, bytes);
                StringBuilder hex = new StringBuilder();
                StringBuilder ascii = new StringBuilder();
                for (int index = 0; index < count; index++) {
                    int value = bytes[index] & 0xff;
                    hex.append(String.format("%02x", value));
                    if (index + 1 < count) hex.append(' ');
                    if (value == 0) break;
                    ascii.append(value >= 32 && value <= 126 ? (char)value : '.');
                }
                writer.println(args[i] + "\t" + ascii + "\t" + hex);
            }
        }
        println("Address dump written to " + output);
    }
}
