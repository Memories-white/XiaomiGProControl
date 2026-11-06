// Extract native bridge strings, cross-references, and selected decompilations.
// @category XiaomiG

import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.data.DataType;
import ghidra.program.model.listing.Data;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;
import ghidra.util.task.ConsoleTaskMonitor;

public class ExtractXiaomiEvidence extends GhidraScript {
    private static final String[] NEEDLES = {
        "performance_mode", "graphic_mode", "cpu_current_info", "gpu_current_info",
        "physical_memory_current_info", "disk_current_info", "network_speed",
        "fn_key", "win_key", "touchpad", "light_effect", "cpu_overclocking",
        "gpu_overclocking", "fan", "temperature", "rpm", "cefquery",
        "business_type", "namedpipe", "\\\\.\\pipe\\"
    };

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length != 1) {
            throw new IllegalArgumentException("Expected one output-file argument");
        }

        File output = new File(args[0]).getAbsoluteFile();
        output.getParentFile().mkdirs();

        List<Function> referencedFunctions = new ArrayList<>();
        Set<Address> seenFunctions = new HashSet<>();

        try (PrintWriter writer = new PrintWriter(output, "UTF-8")) {
            writer.println("PROGRAM\t" + currentProgram.getName());
            writer.println("IMAGE_BASE\t" + currentProgram.getImageBase());
            writer.println("LANGUAGE\t" + currentProgram.getLanguageID());
            writer.println("COMPILER\t" + currentProgram.getCompilerSpec().getCompilerSpecID());

            for (Data data : currentProgram.getListing().getDefinedData(true)) {
                monitor.checkCancelled();
                Object value = data.getValue();
                if (!(value instanceof String)) {
                    continue;
                }

                String text = (String) value;
                String lowered = text.toLowerCase(Locale.ROOT);
                if (!matches(lowered)) {
                    continue;
                }

                DataType type = data.getDataType();
                writer.println("STRING\t" + data.getAddress() + "\t" + type.getName() + "\t" + sanitize(text));

                ReferenceIterator references = currentProgram.getReferenceManager().getReferencesTo(data.getAddress());
                while (references.hasNext()) {
                    Reference reference = references.next();
                    Address from = reference.getFromAddress();
                    Function function = currentProgram.getFunctionManager().getFunctionContaining(from);
                    writer.println("XREF\t" + data.getAddress() + "\t" + from + "\t" +
                        (function == null ? "<no-function>" : function.getEntryPoint() + "\t" + function.getName(true)));
                    if (function != null && seenFunctions.add(function.getEntryPoint())) {
                        referencedFunctions.add(function);
                    }

                    ReferenceIterator secondLevel = currentProgram.getReferenceManager().getReferencesTo(from);
                    while (secondLevel.hasNext()) {
                        Reference indirectReference = secondLevel.next();
                        Address indirectFrom = indirectReference.getFromAddress();
                        Function indirectFunction = currentProgram.getFunctionManager().getFunctionContaining(indirectFrom);
                        writer.println("XREF2\t" + data.getAddress() + "\t" + from + "\t" + indirectFrom + "\t" +
                            (indirectFunction == null ? "<no-function>" : indirectFunction.getEntryPoint() + "\t" + indirectFunction.getName(true)));
                        if (indirectFunction != null && seenFunctions.add(indirectFunction.getEntryPoint())) {
                            referencedFunctions.add(indirectFunction);
                        }
                    }
                }
            }

            writer.println("FUNCTION_COUNT\t" + referencedFunctions.size());
            DecompInterface decompiler = new DecompInterface();
            decompiler.openProgram(currentProgram);
            int emitted = 0;
            for (Function function : referencedFunctions) {
                monitor.checkCancelled();
                if (emitted >= 60) {
                    break;
                }
                DecompileResults result = decompiler.decompileFunction(function, 90, new ConsoleTaskMonitor());
                writer.println("DECOMPILE_BEGIN\t" + function.getEntryPoint() + "\t" + function.getName(true));
                if (result.decompileCompleted() && result.getDecompiledFunction() != null) {
                    writer.println(result.getDecompiledFunction().getC());
                }
                else {
                    writer.println("/* decompile failed: " + sanitize(result.getErrorMessage()) + " */");
                }
                writer.println("DECOMPILE_END\t" + function.getEntryPoint());
                emitted++;
            }
            decompiler.dispose();
        }

        println("Evidence written to " + output);
    }

    private boolean matches(String value) {
        for (String needle : NEEDLES) {
            if (value.contains(needle)) {
                return true;
            }
        }
        return false;
    }

    private String sanitize(String value) {
        if (value == null) {
            return "";
        }
        return value.replace("\r", "\\r").replace("\n", "\\n").replace("\t", "\\t");
    }
}
