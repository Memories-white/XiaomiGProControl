// Decompile selected function entry points into a stable text artifact.
// @category XiaomiG

import java.io.File;
import java.io.PrintWriter;

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.util.task.ConsoleTaskMonitor;

public class DecompileAddresses extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 2) {
            throw new IllegalArgumentException("Expected output file followed by one or more addresses");
        }

        File output = new File(args[0]).getAbsoluteFile();
        output.getParentFile().mkdirs();
        DecompInterface decompiler = new DecompInterface();
        decompiler.openProgram(currentProgram);

        try (PrintWriter writer = new PrintWriter(output, "UTF-8")) {
            for (int i = 1; i < args.length; i++) {
                monitor.checkCancelled();
                Address address = currentProgram.getAddressFactory().getDefaultAddressSpace().getAddress(args[i]);
                Function function = currentProgram.getFunctionManager().getFunctionAt(address);
                if (function == null) {
                    function = currentProgram.getFunctionManager().getFunctionContaining(address);
                }
                writer.println("DECOMPILE_REQUEST\t" + args[i]);
                if (function == null) {
                    writer.println("NO_FUNCTION");
                    continue;
                }

                writer.println("DECOMPILE_BEGIN\t" + function.getEntryPoint() + "\t" + function.getName(true));
                DecompileResults result = decompiler.decompileFunction(function, 120, new ConsoleTaskMonitor());
                if (result.decompileCompleted() && result.getDecompiledFunction() != null) {
                    writer.println(result.getDecompiledFunction().getC());
                }
                else {
                    writer.println("/* decompile failed: " + result.getErrorMessage() + " */");
                }
                writer.println("DECOMPILE_END\t" + function.getEntryPoint());
            }
        }
        decompiler.dispose();
        println("Selected decompilations written to " + output);
    }
}
