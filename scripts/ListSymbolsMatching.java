// List symbols whose qualified names contain any requested substring.
// @category XiaomiG

import java.io.File;
import java.io.PrintWriter;
import java.util.Locale;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.symbol.Symbol;
import ghidra.program.model.symbol.SymbolIterator;

public class ListSymbolsMatching extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 2) throw new IllegalArgumentException("Expected output file and patterns");
        File output = new File(args[0]).getAbsoluteFile();
        output.getParentFile().mkdirs();
        try (PrintWriter writer = new PrintWriter(output, "UTF-8")) {
            SymbolIterator symbols = currentProgram.getSymbolTable().getAllSymbols(true);
            while (symbols.hasNext()) {
                monitor.checkCancelled();
                Symbol symbol = symbols.next();
                String name = symbol.getName(true);
                String lowered = name.toLowerCase(Locale.ROOT);
                for (int i = 1; i < args.length; i++) {
                    if (lowered.contains(args[i].toLowerCase(Locale.ROOT))) {
                        writer.println(symbol.getAddress() + "\t" + symbol.getSymbolType() + "\t" + name);
                        break;
                    }
                }
            }
        }
        println("Symbol list written to " + output);
    }
}
