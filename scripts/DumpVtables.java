// Dump virtual function pointers and resolved functions for selected vtables.
// @category XiaomiG

import java.io.File;
import java.io.PrintWriter;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Symbol;

public class DumpVtables extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 2) throw new IllegalArgumentException("Expected output file and vtable addresses");
        File output = new File(args[0]).getAbsoluteFile();
        output.getParentFile().mkdirs();
        try (PrintWriter writer = new PrintWriter(output, "UTF-8")) {
            for (int i = 1; i < args.length; i++) {
                Address table = currentProgram.getAddressFactory().getDefaultAddressSpace().getAddress(args[i]);
                Symbol tableSymbol = currentProgram.getSymbolTable().getPrimarySymbol(table);
                writer.println("VTABLE\t" + table + "\t" + (tableSymbol == null ? "" : tableSymbol.getName(true)));
                for (int slot = 0; slot < 12; slot++) {
                    Address entry = table.add(slot * 8L);
                    long raw = currentProgram.getMemory().getLong(entry);
                    Address target = currentProgram.getAddressFactory().getDefaultAddressSpace().getAddress(raw);
                    Function function = currentProgram.getFunctionManager().getFunctionAt(target);
                    Symbol symbol = currentProgram.getSymbolTable().getPrimarySymbol(target);
                    writer.println("SLOT\t" + slot + "\t" + entry + "\t" + target + "\t" +
                        (function == null ? "" : function.getName(true)) + "\t" +
                        (symbol == null ? "" : symbol.getName(true)));
                }
            }
        }
        println("Vtable dump written to " + output);
    }
}
