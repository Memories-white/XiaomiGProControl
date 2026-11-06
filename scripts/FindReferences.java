// List code references to one or more addresses.
// @category XiaomiG

import java.io.File;
import java.io.PrintWriter;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;

public class FindReferences extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 2) {
            throw new IllegalArgumentException("Expected output file followed by target addresses");
        }

        File output = new File(args[0]).getAbsoluteFile();
        output.getParentFile().mkdirs();
        try (PrintWriter writer = new PrintWriter(output, "UTF-8")) {
            for (int i = 1; i < args.length; i++) {
                Address target = currentProgram.getAddressFactory().getDefaultAddressSpace().getAddress(args[i]);
                writer.println("TARGET\t" + target);
                ReferenceIterator references = currentProgram.getReferenceManager().getReferencesTo(target);
                while (references.hasNext()) {
                    Reference reference = references.next();
                    Function function = currentProgram.getFunctionManager().getFunctionContaining(reference.getFromAddress());
                    writer.println("REF\t" + reference.getFromAddress() + "\t"
                            + (function == null ? "NO_FUNCTION" : function.getEntryPoint() + "\t" + function.getName(true)));
                }
            }
        }
        println("References written to " + output);
    }
}
