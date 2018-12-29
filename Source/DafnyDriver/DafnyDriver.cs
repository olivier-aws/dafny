//-----------------------------------------------------------------------------
//
// Copyright (C) Microsoft Corporation.  All Rights Reserved.
//
//-----------------------------------------------------------------------------
//---------------------------------------------------------------------------------------------
// DafnyDriver
//       - main program for taking a Dafny program and verifying it
//---------------------------------------------------------------------------------------------

namespace Microsoft.Dafny
{
  using System;
  using System.CodeDom.Compiler;
  using System.Collections.Generic;
  using System.Collections.ObjectModel;
  using System.Diagnostics.Contracts;
  using System.IO;
  using System.Reflection;
  using System.Linq;

  using Microsoft.Boogie;
  using Bpl = Microsoft.Boogie;
  using DafnyAssembly;
  using System.Diagnostics;

  public class DafnyDriver
  {
    public enum ExitValue { VERIFIED = 0, PREPROCESSING_ERROR, DAFNY_ERROR, COMPILE_ERROR, NOT_VERIFIED }

    public static int Main(string[] args)
    {
      int ret = 0;
      var thread = new System.Threading.Thread(
        new System.Threading.ThreadStart(() =>
          { ret = ThreadMain(args); }),
          0x10000000); // 256MB stack size to prevent stack overflow
      thread.Start();
      thread.Join();
      return ret;
    }

    public static int ThreadMain(string[] args)
    {
      Contract.Requires(cce.NonNullElements(args));

      ErrorReporter reporter = new ConsoleErrorReporter();
      ExecutionEngine.printer = new DafnyConsolePrinter(); // For boogie errors

      DafnyOptions.Install(new DafnyOptions(reporter));

      List<DafnyFile> dafnyFiles;
      List<string> otherFiles;

      ExitValue exitValue = ProcessCommandLineArguments(args, out dafnyFiles, out otherFiles);

      if (exitValue == ExitValue.VERIFIED)
      {
        exitValue = ProcessFiles(dafnyFiles, otherFiles.AsReadOnly(), reporter);
      }

      if (CommandLineOptions.Clo.XmlSink != null) {
        CommandLineOptions.Clo.XmlSink.Close();
      }
      if (CommandLineOptions.Clo.Wait)
      {
        Console.WriteLine("Press Enter to exit.");
        Console.ReadLine();
      }
      if (!DafnyOptions.O.CountVerificationErrors && exitValue != ExitValue.PREPROCESSING_ERROR)
      {
        return 0;
      }
      //Console.ReadKey();
      return (int)exitValue;
    }

    public static ExitValue ProcessCommandLineArguments(string[] args, out List<DafnyFile> dafnyFiles, out List<string> otherFiles)
    {
      dafnyFiles = new List<DafnyFile>();
      otherFiles = new List<string>();

      CommandLineOptions.Clo.RunningBoogieFromCommandLine = true;
      if (!CommandLineOptions.Clo.Parse(args)) {
        return ExitValue.PREPROCESSING_ERROR;
      }
      //CommandLineOptions.Clo.Files = new List<string> { @"C:\dafny\Test\dafny0\Trait\TraitExtend.dfy" };

      if (CommandLineOptions.Clo.Files.Count == 0)
      {
        ExecutionEngine.printer.ErrorWriteLine(Console.Out, "*** Error: No input files were specified.");
        return ExitValue.PREPROCESSING_ERROR;
      }
      if (CommandLineOptions.Clo.XmlSink != null) {
        string errMsg = CommandLineOptions.Clo.XmlSink.Open();
        if (errMsg != null) {
          ExecutionEngine.printer.ErrorWriteLine(Console.Out, "*** Error: " + errMsg);
          return ExitValue.PREPROCESSING_ERROR;
        }
      }
      if (!CommandLineOptions.Clo.DontShowLogo)
      {
        Console.WriteLine(CommandLineOptions.Clo.Version);
      }
      if (CommandLineOptions.Clo.ShowEnv == CommandLineOptions.ShowEnvironment.Always)
      {
        Console.WriteLine("---Command arguments");
        foreach (string arg in args)
        {Contract.Assert(arg != null);
          Console.WriteLine(arg);
        }
        Console.WriteLine("--------------------");
      }

      foreach (string file in CommandLineOptions.Clo.Files)
      { Contract.Assert(file != null);
        string extension = Path.GetExtension(file);
        if (extension != null) { extension = extension.ToLower(); }
        try { dafnyFiles.Add(new DafnyFile(file)); } catch (IllegalDafnyFile) {
          if ((extension == ".cs") || (extension == ".dll")) {
            otherFiles.Add(file);
          } else {
            ExecutionEngine.printer.ErrorWriteLine(Console.Out, "*** Error: '{0}': Filename extension '{1}' is not supported. Input files must be Dafny programs (.dfy) or C# files (.cs) or managed DLLS (.dll)", file,
              extension == null ? "" : extension);
            return ExitValue.PREPROCESSING_ERROR;
          }
        }
      }
      return ExitValue.VERIFIED;
    }

    static ExitValue ProcessFiles(IList<DafnyFile/*!*/>/*!*/ dafnyFiles, ReadOnlyCollection<string> otherFileNames, 
                                  ErrorReporter reporter, bool lookForSnapshots = true, string programId = null)
   {
      Contract.Requires(cce.NonNullElements(dafnyFiles));
      var dafnyFileNames = DafnyFile.fileNames(dafnyFiles);

      ExitValue exitValue = ExitValue.VERIFIED;
      if (CommandLineOptions.Clo.VerifySeparately && 1 < dafnyFiles.Count)
      {
        foreach (var f in dafnyFiles)
        {
          Console.WriteLine();
          Console.WriteLine("-------------------- {0} --------------------", f);
          var ev = ProcessFiles(new List<DafnyFile> { f }, new List<string>().AsReadOnly(), reporter, lookForSnapshots, f.FilePath);
          if (exitValue != ev && ev != ExitValue.VERIFIED)
          {
            exitValue = ev;
          }
        }
        return exitValue;
      }

      if (0 <= CommandLineOptions.Clo.VerifySnapshots && lookForSnapshots)
      {
        var snapshotsByVersion = ExecutionEngine.LookForSnapshots(dafnyFileNames);
        foreach (var s in snapshotsByVersion)
        {
          var snapshots = new List<DafnyFile>();
          foreach (var f in s) {
            snapshots.Add(new DafnyFile(f));
          }
          var ev = ProcessFiles(snapshots, new List<string>().AsReadOnly(), reporter, false, programId);
          if (exitValue != ev && ev != ExitValue.VERIFIED)
          {
            exitValue = ev;
          }
        }
        return exitValue;
      }
      
      Dafny.Program dafnyProgram;
      string programName = dafnyFileNames.Count == 1 ? dafnyFileNames[0] : "the program";
      string err = Dafny.Main.ParseCheck(dafnyFiles, programName, reporter, out dafnyProgram);
      if (err != null) {
        exitValue = ExitValue.DAFNY_ERROR;
        ExecutionEngine.printer.ErrorWriteLine(Console.Out, err);
      } else if (dafnyProgram != null && !CommandLineOptions.Clo.NoResolve && !CommandLineOptions.Clo.NoTypecheck
          && DafnyOptions.O.DafnyVerify) {

        var boogiePrograms = Translate(dafnyProgram);

        Dictionary<string, PipelineStatistics> statss;
        PipelineOutcome oc;
        string baseName = cce.NonNull(Path.GetFileName(dafnyFileNames[dafnyFileNames.Count - 1]));
        var verified = Boogie(baseName, boogiePrograms, programId, out statss, out oc);
        var compiled = Compile(dafnyFileNames[0], otherFileNames, dafnyProgram, oc, statss, verified);
        exitValue = verified && compiled ? ExitValue.VERIFIED : !verified ? ExitValue.NOT_VERIFIED : ExitValue.COMPILE_ERROR;
      }

      if (err == null && dafnyProgram != null && DafnyOptions.O.PrintStats) {
        Util.PrintStats(dafnyProgram);
      }
      if (err == null && dafnyProgram != null && DafnyOptions.O.PrintFunctionCallGraph) {
        Util.PrintFunctionCallGraph(dafnyProgram);
      }
      return exitValue;
    }

    private static string BoogieProgramSuffix(string printFile, string suffix) {
      var baseName = Path.GetFileNameWithoutExtension(printFile);
      var dirName = Path.GetDirectoryName(printFile);

      return Path.Combine(dirName, baseName + "_" + suffix + Path.GetExtension(printFile));
    }

    public static IEnumerable<Tuple<string, Bpl.Program>> Translate(Program dafnyProgram) {
      var nmodules = Translator.VerifiableModules(dafnyProgram).Count();


      foreach (var prog in Translator.Translate(dafnyProgram, dafnyProgram.reporter)) {

        if (CommandLineOptions.Clo.PrintFile != null) {

          var nm = nmodules > 1 ? BoogieProgramSuffix(CommandLineOptions.Clo.PrintFile, prog.Item1) : CommandLineOptions.Clo.PrintFile;

          ExecutionEngine.PrintBplFile(nm, prog.Item2, false, false, CommandLineOptions.Clo.PrettyPrint);
        }

        yield return prog;

      }
    }

    public static bool BoogieOnce(string baseFile, string moduleName, Bpl.Program boogieProgram, string programId,
                              out PipelineStatistics stats, out PipelineOutcome oc)
    {
      if (programId == null)
      {
        programId = "main_program_id";
      }
      programId += "_" + moduleName;

      string bplFilename;
      if (CommandLineOptions.Clo.PrintFile != null)
      {
        bplFilename = CommandLineOptions.Clo.PrintFile;
      }
      else
      {
        string baseName = cce.NonNull(Path.GetFileName(baseFile));
        baseName = cce.NonNull(Path.ChangeExtension(baseName, "bpl"));
        bplFilename = Path.Combine(Path.GetTempPath(), baseName);
      }

      bplFilename = BoogieProgramSuffix(bplFilename, moduleName);
      stats = null;
      oc = BoogiePipelineWithRerun(boogieProgram, bplFilename, out stats, 1 < Dafny.DafnyOptions.Clo.VerifySnapshots ? programId : null);
      return (oc == PipelineOutcome.Done || oc == PipelineOutcome.VerificationCompleted) && stats.ErrorCount == 0 && stats.InconclusiveCount == 0 && stats.TimeoutCount == 0 && stats.OutOfMemoryCount == 0;
    }

    public static bool Boogie(string baseName, IEnumerable<Tuple<string, Bpl.Program>> boogiePrograms, string programId, out Dictionary<string, PipelineStatistics> statss, out PipelineOutcome oc) {

      bool isVerified = true;
      oc = PipelineOutcome.VerificationCompleted;
      statss = new Dictionary<string, PipelineStatistics>();

      Stopwatch watch = new Stopwatch();
      watch.Start();

      foreach (var prog in boogiePrograms) {
        PipelineStatistics newstats;
        PipelineOutcome newoc;

        if (DafnyOptions.O.SeparateModuleOutput) {
          ExecutionEngine.printer.AdvisoryWriteLine("For module: {0}", prog.Item1);
        }

        isVerified = BoogieOnce(baseName, prog.Item1, prog.Item2, programId, out newstats, out newoc) && isVerified;

        watch.Stop();

        if ((oc == PipelineOutcome.VerificationCompleted || oc == PipelineOutcome.Done) && newoc != PipelineOutcome.VerificationCompleted) {
          oc = newoc;
        }

        if (DafnyOptions.O.SeparateModuleOutput) {
          TimeSpan ts = watch.Elapsed;
          string elapsedTime = String.Format("{0:00}:{1:00}:{2:00}",
            ts.Hours, ts.Minutes, ts.Seconds);

          ExecutionEngine.printer.AdvisoryWriteLine("Elapsed time: {0}", elapsedTime);
          ExecutionEngine.printer.WriteTrailer(newstats);
        }

        statss.Add(prog.Item1, newstats);
        watch.Restart();
      }
      watch.Stop();

      return isVerified;
    }

    private static void WriteStatss(Dictionary<string, PipelineStatistics> statss) {
      var statSum = new PipelineStatistics();
      foreach (var stats in statss) {
        statSum.VerifiedCount += stats.Value.VerifiedCount;
        statSum.ErrorCount += stats.Value.ErrorCount;
        statSum.TimeoutCount += stats.Value.TimeoutCount;
        statSum.OutOfMemoryCount += stats.Value.OutOfMemoryCount;
        statSum.CachedErrorCount += stats.Value.CachedErrorCount;
        statSum.CachedInconclusiveCount += stats.Value.CachedInconclusiveCount;
        statSum.CachedOutOfMemoryCount += stats.Value.CachedOutOfMemoryCount;
        statSum.CachedTimeoutCount += stats.Value.CachedTimeoutCount;
        statSum.CachedVerifiedCount += stats.Value.CachedVerifiedCount;
        statSum.InconclusiveCount += stats.Value.InconclusiveCount;        
      }
      ExecutionEngine.printer.WriteTrailer(statSum);
    }


    public static bool Compile(string fileName, ReadOnlyCollection<string> otherFileNames, Program dafnyProgram,
                               PipelineOutcome oc, Dictionary<string, PipelineStatistics> statss, bool verified)
    {
      var resultFileName = DafnyOptions.O.DafnyPrintCompiledFile ?? fileName;
      bool compiled = true;
      switch (oc)
      {
        case PipelineOutcome.VerificationCompleted:
          WriteStatss(statss);
          if ((DafnyOptions.O.Compile && verified && CommandLineOptions.Clo.ProcsToCheck == null) || DafnyOptions.O.ForceCompile) {
            compiled = CompileDafnyProgram(dafnyProgram, resultFileName, otherFileNames, true);
          } else if ((2 <= DafnyOptions.O.SpillTargetCode && verified && CommandLineOptions.Clo.ProcsToCheck == null) || 3 <= DafnyOptions.O.SpillTargetCode) {
            compiled = CompileDafnyProgram(dafnyProgram, resultFileName, otherFileNames, false);
          }
          break;
        case PipelineOutcome.Done:
          WriteStatss(statss);
          if (DafnyOptions.O.ForceCompile || 3 <= DafnyOptions.O.SpillTargetCode) {
            compiled = CompileDafnyProgram(dafnyProgram, resultFileName, otherFileNames, DafnyOptions.O.ForceCompile);
          }
          break;
        default:
          // error has already been reported to user
          break;
      }
      return compiled;
    }

    /// <summary>
    /// Resolve, type check, infer invariants for, and verify the given Boogie program.
    /// The intention is that this Boogie program has been produced by translation from something
    /// else.  Hence, any resolution errors and type checking errors are due to errors in
    /// the translation.
    /// The method prints errors for resolution and type checking errors, but still returns
    /// their error code.
    /// </summary>
    static PipelineOutcome BoogiePipelineWithRerun(Bpl.Program/*!*/ program, string/*!*/ bplFileName,
        out PipelineStatistics stats, string programId)
    {
      Contract.Requires(program != null);
      Contract.Requires(bplFileName != null);
      Contract.Ensures(0 <= Contract.ValueAtReturn(out stats).InconclusiveCount && 0 <= Contract.ValueAtReturn(out stats).TimeoutCount);

      stats = new PipelineStatistics();
      LinearTypeChecker ltc;
      CivlTypeChecker ctc;
      PipelineOutcome oc = ExecutionEngine.ResolveAndTypecheck(program, bplFileName, out ltc, out ctc);
      switch (oc) {
        case PipelineOutcome.Done:
          return oc;

        case PipelineOutcome.ResolutionError:
        case PipelineOutcome.TypeCheckingError:
          {
            ExecutionEngine.PrintBplFile(bplFileName, program, false, false, CommandLineOptions.Clo.PrettyPrint);
            Console.WriteLine();
            Console.WriteLine("*** Encountered internal translation error - re-running Boogie to get better debug information");
            Console.WriteLine();

            List<string/*!*/>/*!*/ fileNames = new List<string/*!*/>();
            fileNames.Add(bplFileName);
            Bpl.Program reparsedProgram = ExecutionEngine.ParseBoogieProgram(fileNames, true);
            if (reparsedProgram != null) {
              ExecutionEngine.ResolveAndTypecheck(reparsedProgram, bplFileName, out ltc, out ctc);
            }
          }
          return oc;

        case PipelineOutcome.ResolvedAndTypeChecked:
          ExecutionEngine.EliminateDeadVariables(program);
          ExecutionEngine.CollectModSets(program);
          ExecutionEngine.CoalesceBlocks(program);
          ExecutionEngine.Inline(program);
          return ExecutionEngine.InferAndVerify(program, stats, programId);

        default:
          Contract.Assert(false); throw new cce.UnreachableException();  // unexpected outcome
      }
    }


    #region Output
    
    class DafnyConsolePrinter : ConsolePrinter
    {
      public override void ReportBplError(IToken tok, string message, bool error, TextWriter tw, string category = null)
      {
        // Dafny has 0-indexed columns, but Boogie counts from 1
        var realigned_tok = new Token(tok.line, tok.col - 1);
        realigned_tok.kind = tok.kind;
        realigned_tok.pos = tok.pos;
        realigned_tok.val = tok.val;
        realigned_tok.filename = tok.filename;
        base.ReportBplError(realigned_tok, message, error, tw, category);

        if (tok is Dafny.NestedToken)
        {
          var nt = (Dafny.NestedToken)tok;
          ReportBplError(nt.Inner, "Related location", false, tw);
        }
      }
    }

    #endregion


    #region Compilation

    static string WriteDafnyProgramToFile(string dafnyProgramName, string targetProgram, bool completeProgram, TextWriter outputWriter)
    {
      string targetFilename = Path.ChangeExtension(dafnyProgramName, DafnyOptions.O.CompileTarget == DafnyOptions.CompilationTarget.JavaScript ? "js" : "cs");
      using (TextWriter target = new StreamWriter(new FileStream(targetFilename, System.IO.FileMode.Create))) {
        target.Write(targetProgram);
        string relativeTarget = Path.GetFileName(targetFilename);
        if (completeProgram) {
          outputWriter.WriteLine("Compiled program written to {0}", relativeTarget);
        }
        else {
          outputWriter.WriteLine("File {0} contains the partially compiled program", relativeTarget);
        }
      }
      return targetFilename;
    }

    /// <summary>
    /// Generate a C# program from the Dafny program and, if "invokeCsCompiler" is "true", invoke
    /// the C# compiler to compile it.
    /// </summary>
    public static bool CompileDafnyProgram(Dafny.Program dafnyProgram, string dafnyProgramName,
                                           ReadOnlyCollection<string> otherFileNames, bool invokeCsCompiler,
                                           TextWriter outputWriter = null)
    {
      Contract.Requires(dafnyProgram != null);

      if (outputWriter == null)
      {
        outputWriter = Console.Out;
      }

      // Compile the Dafny program into a string that contains the C# program
      var oldErrorCount = dafnyProgram.reporter.Count(ErrorLevel.Error);
      Dafny.Compiler compiler;
      switch (DafnyOptions.O.CompileTarget) {
        case DafnyOptions.CompilationTarget.Csharp:
        default:
          compiler = new Dafny.CsharpCompiler(dafnyProgram.reporter);
          break;
        case DafnyOptions.CompilationTarget.JavaScript:
          compiler = new Dafny.JavaScriptCompiler(dafnyProgram.reporter);
          break;
      }
      
      var hasMain = compiler.HasMain(dafnyProgram);
      var sw = new TargetWriter();
      compiler.Compile(dafnyProgram, sw);
      var csharpProgram = sw.ToString();
      bool completeProgram = dafnyProgram.reporter.Count(ErrorLevel.Error) == oldErrorCount;

      // blurt out the code to a file, if requested, or if other files were specified for the C# command line.
      string targetFilename = null;
      if (DafnyOptions.O.SpillTargetCode > 0 || otherFileNames.Count > 0)
      {
        targetFilename = WriteDafnyProgramToFile(dafnyProgramName, csharpProgram, completeProgram, outputWriter);
      }

      // compile the program into an assembly
      if (!completeProgram || !invokeCsCompiler)
      {
        // don't compile
        return false;
      }
      else if (!CodeDomProvider.IsDefinedLanguage("CSharp"))
      {
        outputWriter.WriteLine("Error: cannot compile, because there is no provider configured for input language CSharp");
        return false;
      }
      else
      {
        var provider = CodeDomProvider.CreateProvider("CSharp", new Dictionary<string, string> { { "CompilerVersion", "v4.0" } });
        var cp = new System.CodeDom.Compiler.CompilerParameters();
        cp.GenerateExecutable = hasMain;
        if (DafnyOptions.O.RunAfterCompile) {
          cp.GenerateInMemory = true;
        } else if (hasMain) {
          cp.OutputAssembly = Path.ChangeExtension(dafnyProgramName, "exe");
          cp.GenerateInMemory = false;
        } else {
          cp.OutputAssembly = Path.ChangeExtension(dafnyProgramName, "dll");
          cp.GenerateInMemory = false;
        }
        cp.CompilerOptions = "/debug /nowarn:0164 /nowarn:0219 /nowarn:1717 /nowarn:0162";  // warning CS0164 complains about unreferenced labels, CS0219 is about unused variables, CS1717 is about assignments of a variable to itself, CS0162 is about unreachable code
        cp.ReferencedAssemblies.Add("System.Numerics.dll");
        cp.ReferencedAssemblies.Add("System.Core.dll");
        cp.ReferencedAssemblies.Add("System.dll");

        var libPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) + Path.DirectorySeparatorChar;
        if (DafnyOptions.O.UseRuntimeLib) {
          cp.ReferencedAssemblies.Add(libPath + "DafnyRuntime.dll");
        }

        var immutableDllFileName = "System.Collections.Immutable.dll";
        var immutableDllPath = libPath + immutableDllFileName;

        if (DafnyOptions.O.Optimize) {
          cp.CompilerOptions += " /optimize /define:DAFNY_USE_SYSTEM_COLLECTIONS_IMMUTABLE";
          cp.ReferencedAssemblies.Add(immutableDllPath);
          cp.ReferencedAssemblies.Add("System.Runtime.dll");
        }

        int numOtherSourceFiles = 0;
        if (otherFileNames.Count > 0) {
          foreach (var file in otherFileNames) {
            string extension = Path.GetExtension(file);
            if (extension != null) { extension = extension.ToLower(); }
            if (extension == ".cs") {
              numOtherSourceFiles++;
            }
            else if (extension == ".dll") {
              cp.ReferencedAssemblies.Add(file);
            }
          }
        }

        CompilerResults cr;
        if (numOtherSourceFiles > 0) {
          string[] sourceFiles = new string[numOtherSourceFiles + 1];
          sourceFiles[0] = targetFilename;
          int index = 1;
          foreach (var file in otherFileNames) {
            string extension = Path.GetExtension(file);
            if (extension != null) { extension = extension.ToLower(); }
            if (extension == ".cs") {
              sourceFiles[index++] = file;
            }
          }
          cr = provider.CompileAssemblyFromFile(cp, sourceFiles);
        }
        else {
          cr = provider.CompileAssemblyFromSource(cp, csharpProgram);
        }

        if (DafnyOptions.O.RunAfterCompile && !hasMain) {
          // do no more
          return cr.Errors.Count == 0 ? true : false;
        }

        var assemblyName = Path.GetFileName(cr.PathToAssembly);
        if (DafnyOptions.O.RunAfterCompile && cr.Errors.Count == 0) {
          outputWriter.WriteLine("Program compiled successfully");
          outputWriter.WriteLine("Running...");
          outputWriter.WriteLine();
          var entry = cr.CompiledAssembly.EntryPoint;
          try {
            object[] parameters = entry.GetParameters().Length == 0 ? new object[] { } : new object[] { new string[0] };
            entry.Invoke(null, parameters);
          } catch (System.Reflection.TargetInvocationException e) {
            outputWriter.WriteLine("Error: Execution resulted in exception: {0}", e.Message);
            outputWriter.WriteLine(e.InnerException.ToString());
          } catch (Exception e) {
            outputWriter.WriteLine("Error: Execution resulted in exception: {0}", e.Message);
            outputWriter.WriteLine(e.ToString());
          }
        } else if (cr.Errors.Count == 0) {
          outputWriter.WriteLine("Compiled assembly into {0}", assemblyName);
          if (DafnyOptions.O.Optimize) {
            var outputDir = Path.GetDirectoryName(dafnyProgramName);
            if (string.IsNullOrWhiteSpace(outputDir)) {
              outputDir = ".";
            }
            var destPath = outputDir + Path.DirectorySeparatorChar + immutableDllFileName;
            File.Copy(immutableDllPath, destPath, true);
            outputWriter.WriteLine("Copied /optimize dependency {0} to {1}", immutableDllFileName, outputDir);
          }
        } else {
          outputWriter.WriteLine("Errors compiling program into {0}", assemblyName);
          foreach (var ce in cr.Errors) {
            outputWriter.WriteLine(ce.ToString());
            outputWriter.WriteLine();
          }
          return false;
        }
        return true;
      }
    }

    #endregion

  }
}
