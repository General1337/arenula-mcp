using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace Arenula.Tests;

/// <summary>
/// Pure contract regression for compile.check's stock-engine receipt. This does
/// not trigger a compile: it proves the classifier never turns a sticky failed
/// BuildResult with zero diagnostics into a fresh local error or a live hotload.
/// </summary>
public static class CompileReceiptContractTest
{
    [ConCmd( "arenula.test_compile_receipt_contract" )]
    public static void Run()
    {
        var gates = new List<(string name, bool pass, string detail)>
        {
            ("clean_source_is_clean",
                CompileHandler.ClassifyOverallReceipt( true, 0, false ) == "clean",
                CompileHandler.ClassifyOverallReceipt( true, 0, false )),
            ("concrete_errors_are_errors",
                CompileHandler.ClassifyOverallReceipt( true, 2, true ) == "errors",
                CompileHandler.ClassifyOverallReceipt( true, 2, true )),
            ("failed_without_diagnostics_is_unknown",
                CompileHandler.ClassifyOverallReceipt( true, 0, true ) == "unknown",
                CompileHandler.ClassifyOverallReceipt( true, 0, true )),
            ("missing_source_is_unknown",
                CompileHandler.ClassifyOverallReceipt( false, 0, false ) == "unknown",
                CompileHandler.ClassifyOverallReceipt( false, 0, false )),
            ("sticky_failure_hotload_is_unknown",
                CompileHandler.ClassifyNoBuildHotloadVerdict( true ) == "unknown",
                CompileHandler.ClassifyNoBuildHotloadVerdict( true )),
            ("successful_noop_hotload_is_live",
                CompileHandler.ClassifyNoBuildHotloadVerdict( false ) == "live",
                CompileHandler.ClassifyNoBuildHotloadVerdict( false ))
        };

        foreach ( var (name, pass, detail) in gates )
            Log.Info( $"  [{(pass ? "PASS" : "FAIL")}] {name} — got={detail}" );

        var failures = gates.Count( gate => !gate.pass );
        if ( failures == 0 ) Log.Info( $"arenula.test_compile_receipt_contract: PASS ({gates.Count} gates)" );
        else Log.Error( $"arenula.test_compile_receipt_contract: FAIL ({failures} of {gates.Count})" );
    }
}
