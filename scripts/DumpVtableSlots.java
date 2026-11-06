// Dump a caller-selected number of virtual function pointers.
// @category XiaomiG

import java.io.File;
import java.io.PrintWriter;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Symbol;

public class DumpVtableSlots extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length != 3) {
            throw new IllegalArgumentException("Expected output file, vtable address and slot count");
        }

        File output = new File(args[0]).getAbsoluteFile();
        output.getParentFile().mkdirs();
        Address table = currentProgram.getAddressFactory().getDefaultAddressSpace().getAddress(args[1]);
        int count = Integer.parseInt(args[2]);
        try (PrintWriter writer = new PrintWriter(output, "UTF-8")) {
            for (int slot = 0; slot < count; slot++) {
                Address entry = table.add(slot * 8L);
                long raw = currentProgram.getMemory().getLong(entry);
                Address target = currentProgram.getAddressFactory().getDefaultAddressSpace().getAddress(raw);
                Function function = currentProgram.getFunctionManager().getFunctionAt(target);
                Symbol symbol = currentProgram.getSymbolTable().getPrimarySymbol(target);
                writer.println("SLOT\t" + slot + "\t" + entry + "\t" + target + "\t"
                    + (function == null ? "" : function.getName(true)) + "\t"
                    + (symbol == null ? "" : symbol.getName(true)));
            }
        }
        println("Vtable slots written to " + output);
    }
}
