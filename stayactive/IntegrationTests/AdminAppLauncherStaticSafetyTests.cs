using System.Text.RegularExpressions;

namespace stayactive.IntegrationTests;

public sealed class AdminAppLauncherStaticSafetyTests
{
    [Fact]
    public void PrepareAndLaunch_UsesExistingStayActiveOutputBeforePreparationOrProcessStop()
    {
        var services = ReadRepositoryFile("AdminPanel", "AdminAppServices.cs");
        var prepareAndLaunch = ExtractBraceBlock(
            services,
            "public static Task<LaunchResult> PrepareAndLaunchAsync(AdminAppDefinition app)");

        var existingOutputLaunch = prepareAndLaunch.IndexOf(
            "TryLaunchCurrentNativeExecutable(",
            StringComparison.Ordinal);
        var dependencyPreparation = prepareAndLaunch.IndexOf(
            "TryPrepareDependencies(",
            StringComparison.Ordinal);
        var processStop = prepareAndLaunch.IndexOf(
            "TryStopExistingProcesses(",
            StringComparison.Ordinal);
        var batchFallback = prepareAndLaunch.IndexOf(
            "TryStart(",
            StringComparison.Ordinal);

        Assert.True(
            existingOutputLaunch >= 0,
            "Admin launch must first try the existing StayActive executable.");
        Assert.True(
            dependencyPreparation > existingOutputLaunch,
            "Dependency preparation must not run before the existing executable is tried.");
        Assert.True(
            processStop > existingOutputLaunch,
            "The live tray/native-host processes must not be stopped before the existing executable is tried.");
        Assert.True(
            batchFallback > processStop,
            "Any batch build fallback must run only after matching processes are stopped and awaited.");

        var successfulExistingLaunch = prepareAndLaunch.IndexOf(
            "if (launched)",
            existingOutputLaunch,
            StringComparison.Ordinal);
        var successfulExistingReturn = prepareAndLaunch.IndexOf(
            "return new LaunchResult(true, string.Empty);",
            successfulExistingLaunch,
            StringComparison.Ordinal);
        Assert.True(successfulExistingLaunch > existingOutputLaunch);
        Assert.True(
            successfulExistingReturn > successfulExistingLaunch
            && successfulExistingReturn < dependencyPreparation,
            "Launching an existing output must short-circuit before restore, stop, or build work.");
    }

    [Fact]
    public void ExistingNativeLaunch_DoesNotRejectOutputBecauseSourcesAreNewer()
    {
        var services = ReadRepositoryFile("AdminPanel", "AdminAppServices.cs");
        var nativeLaunch = ExtractBraceBlock(
            services,
            "private static bool TryLaunchCurrentNativeExecutable(");

        Assert.Contains("File.Exists(executablePath)", nativeLaunch, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IsBuildOutputCurrent",
            nativeLaunch,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "EnumerateBuildInputs",
            nativeLaunch,
            StringComparison.Ordinal);
        Assert.Contains(
            "Process.Start(new ProcessStartInfo",
            nativeLaunch,
            StringComparison.Ordinal);
        Assert.Contains("FileName = executablePath", nativeLaunch, StringComparison.Ordinal);
    }

    [Fact]
    public void StayActiveBatch_BuildsOnlyWhenExecutableIsMissingThenAlwaysStartsIt()
    {
        var batch = ReadRepositoryFile("stayactive", "start.bat");

        Assert.DoesNotContain("tasklist", batch, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IMAGENAME", batch, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "find /I \"stayactive.exe\"",
            batch,
            StringComparison.OrdinalIgnoreCase);

        Assert.Matches(
            new Regex(
                """
                (?is)if\s+not\s+exist\s+"%APP%"\s*\(\s*
                .*?call\s+"%APP_DIR%\.\.\\scripts\\ensure-dotnet-sdk\.bat"\s+10
                .*?"%DOTNET_EXE%"\s+build\s+"%PROJECT%"\s+-c\s+Release
                .*?\)\s*
                if\s+not\s+exist\s+"%APP%"\s+exit\s+/b\s+1\s*
                start\s+"StayActive"\s+/d\s+"%APP_DIR%"\s+"%APP%"\s*
                """,
                RegexOptions.IgnorePatternWhitespace),
            batch);
        Assert.Single(
            Regex.Matches(
                    batch,
                    @"(?im)^\s*call\s+""%APP_DIR%\.\.\\scripts\\ensure-dotnet-sdk\.bat""\s+10\s*$")
                .Cast<Match>());
        Assert.Single(
            Regex.Matches(
                    batch,
                    @"(?im)^\s*""%DOTNET_EXE%""\s+build\b")
                .Cast<Match>());
        Assert.Single(
            Regex.Matches(
                    batch,
                    @"(?im)^\s*start\s+""StayActive""(?=\s|$)")
                .Cast<Match>());
    }

    [Fact]
    public void StayActiveDependencyPreparation_RestoresButNeverBuildsLiveOutput()
    {
        var dependencyScript = ReadRepositoryFile(
            "scripts",
            "ensure-admin-app-dependencies.bat");
        var stayActiveMatch = Regex.Match(
            dependencyScript,
            @"(?ims)^:stayActive\s*(?<body>.*?)(?=^:[A-Za-z])");
        Assert.True(stayActiveMatch.Success, "The :stayActive dependency branch was not found.");
        var stayActiveBranch = stayActiveMatch.Groups["body"].Value;
        Assert.Contains(
            "call :restoreDotnet \"%APP_DIR%\\stayactive.csproj\"",
            stayActiveBranch,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            " build ",
            dependencyScript,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "\"%DOTNET_EXE%\" restore \"%PROJECT%\"",
            dependencyScript,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StayActiveNativeHostAndTrayCanCoexistWhileTrayRemainsSingleton()
    {
        var program = ReadRepositoryFile("stayactive", "Program.cs");
        var main = ExtractBraceBlock(program, "private static void Main(string[] args)");
        var nativeHost = main.IndexOf(
            "ChromeCursorInputNativeMessagingHost.TryRun(args)",
            StringComparison.Ordinal);
        var singleton = main.IndexOf(
            "new Mutex(true, \"StayActive.Singleton\"",
            StringComparison.Ordinal);

        Assert.True(nativeHost >= 0);
        Assert.True(
            singleton > nativeHost,
            "Chrome's native host must bypass the tray singleton, while zero-argument tray launches remain idempotent.");
        Assert.Matches(
            new Regex(
                """
                (?s)if\s*\(\s*ChromeCursorInputNativeMessagingHost\.TryRun\(args\)\s*\)
                \s*\{\s*return;\s*\}
                .*?
                new\s+Mutex\(\s*true,\s*"StayActive\.Singleton",\s*out\s+var\s+createdNew\s*\)
                \s*;\s*
                if\s*\(\s*!createdNew\s*\)\s*\{\s*return;\s*\}
                """,
                RegexOptions.IgnorePatternWhitespace),
            main);
    }

    private static string ReadRepositoryFile(params string[] relativePathParts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(
                new[] { current.FullName }
                    .Concat(relativePathParts)
                    .ToArray());
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            current = current.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {Path.Combine(relativePathParts)} from {AppContext.BaseDirectory}.");
    }

    private static string ExtractBraceBlock(string source, string declaration)
    {
        var declarationIndex = source.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(declarationIndex >= 0, $"Declaration was not found: {declaration}");

        var openingBraceIndex = source.IndexOf('{', declarationIndex);
        Assert.True(openingBraceIndex >= 0, $"Opening brace was not found: {declaration}");

        var depth = 0;
        for (var index = openingBraceIndex; index < source.Length; index++)
        {
            switch (source[index])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return source[declarationIndex..(index + 1)];
                    }
                    break;
            }
        }

        throw new InvalidOperationException($"Closing brace was not found: {declaration}");
    }
}
