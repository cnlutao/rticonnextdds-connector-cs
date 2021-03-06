// (c) 2018 Copyright, Real-Time Innovations, All rights reserved.
//
// RTI grants Licensee a license to use, modify, compile, and create
// derivative works of the Software.  Licensee has the right to distribute
// object form only for use with RTI products. The Software is provided
// "as is", with no warranty of any type, including any warranty for fitness
// for any purpose. RTI is under no obligation to maintain or support the
// Software.  RTI shall not be liable for any incidental or consequential
// damages arising out of the use or inability to use the software.

// NUnit tests
#tool nuget:?package=NUnit.ConsoleRunner&version=3.9.0

// Gendarme: decompress zip
#addin nuget:?package=Cake.Compression&loaddependencies=true&version=0.2.1


// Test coverage
#addin nuget:?package=altcover.api&version=5.0.663
#tool nuget:?package=ReportGenerator&version=4.0.5

// Documentation
#addin nuget:?package=Cake.DocFx&version=0.11.0
#tool nuget:?package=docfx.console&version=2.40.7

// For the logic to detect Mac OS
using System.Runtime.InteropServices;

var netFrameworkVersion = "net45";
var netCoreVersion = "netcoreapp2.2";
var netStandardVersion = "netstandard1.1";

var target = Argument("target", "Default");
var configuration = Argument("configuration", "Debug");
var warningAsErrors = Argument("warnaserror", true);

var msbuildConfig = new MSBuildSettings {
    Verbosity = Verbosity.Minimal,
    Configuration = configuration,
    Restore = true,
    MaxCpuCount = 0,  // Auto build parallel mode
    WarningsAsError = warningAsErrors,
};


Task("Clean")
    .Does(() =>
{
    MSBuild("src/Connector.sln", configurator => configurator
        .WithTarget("Clean")
        .SetVerbosity(Verbosity.Minimal)
        .SetConfiguration(configuration));
    if (DirectoryExists("artifacts")) {
        DeleteDirectory(
            "artifacts",
            new DeleteDirectorySettings { Recursive = true });
    }
});

Task("Build-API")
    .Description("Build the API")
    .Does(() =>
{
    MSBuild("src/Connector.sln", msbuildConfig);
});

Task("Build-Examples")
    .Description("Build example projects")
    .IsDependentOn("Build-API")
    .Does(() =>
{
    MSBuild("examples/Simple/Simple.sln", msbuildConfig);
    MSBuild("examples/Mixed/Mixed.sln", msbuildConfig);
    MSBuild("examples/Objects/Objects.sln", msbuildConfig);
});

Task("Run-UnitTests")
    .Description("Run unit tests")
    .IsDependentOn("Build-API")
    .Does(() =>
{
    string testProjectDir = "src/Connector.UnitTests";
    string testProject = $"{testProjectDir}/Connector.UnitTests.csproj";
    var environment = new Dictionary<string, string> {
        { GetLoadLibraryEnvVar(), GetNativeLibraryPath() }
    };

    // NUnit3 to test libraries with .NET Framework / Mono
    var nunitSettings = new NUnit3Settings {
        EnvironmentVariables = environment
    };
    NUnit3(
        $"{testProjectDir}/bin/{configuration}/{netFrameworkVersion}/*.UnitTests.dll",
        nunitSettings);

    // .NET Core test library
    var netcoreSettings = new DotNetCoreTestSettings {
        EnvironmentVariables = environment,
        NoBuild = true,
        Framework = netCoreVersion
    };
    DotNetCoreTest(testProject, netcoreSettings);
});

Task("Run-Linter-Gendarme")
    .Description("Run static analyzer Gendarme")
    .IsDependentOn("Build-API")
    .Does(() =>
{
    if (IsRunningOnWindows()) {
        throw new Exception("Gendarme is not supported on Windows");
    }

    var monoTools = DownloadFile("https://github.com/pleonex/mono-tools/releases/download/v4.2.2/mono-tools-v4.2.2.zip");
    ZipUncompress(monoTools, "tools/mono_tools");
    var gendarme = "tools/mono_tools/bin/gendarme";
    if (StartProcess("chmod", $"+x {gendarme}") != 0) {
        Error("Cannot change gendarme permissions");
    }

    RunGendarme(
        gendarme,
        $"src/Connector/bin/{configuration}/{netStandardVersion}/RTI.Connext.Connector.dll",
        "src/Connector/Gendarme.ignore");
});

Task("Run-AltCover")
    .Description("Run test coverage with AltCover")
    .IsDependentOn("Build-API")
    .Does(() =>
{
    // Configure the tests to run with code coverate
    TestWithAltCover(
        "src/Connector.UnitTests",
        "RTI.Connext.Connector.UnitTests.dll",
        "coverage.xml");

    // Create the report
    var reportTypes = new[] {
        ReportGeneratorReportType.Html,
        ReportGeneratorReportType.XmlSummary };
    ReportGenerator(
        "coverage.xml",
        "coverage_report",
        new ReportGeneratorSettings { ReportTypes = reportTypes });

    // Get final result
    var xml = System.Xml.Linq.XDocument.Load("coverage_report/Summary.xml");
    var xmlSummary = xml.Root.Element("Summary");
    var covered = xmlSummary.Element("Coveredlines").Value;
    var coverable = xmlSummary.Element("Coverablelines").Value;
    if (covered == coverable) {
        Information("Full coverage!");
    } else {
        ReportWarning($"Missing coverage: {covered} of {coverable}");
    }
});

Task("Fix-DocFx")
    .Description("Workaround for issue #3389: missing dependency")
    .Does(() =>
{
    // Workaround for
    // https://github.com/dotnet/docfx/issues/3389
    NuGetInstall("SQLitePCLRaw.core", new NuGetInstallSettings {
        ExcludeVersion  = true,
        OutputDirectory = "./tools"
    });

    CopyFileToDirectory(
        "tools/SQLitePCLRaw.core/lib/net45/SQLitePCLRaw.core.dll",
        GetDirectories("tools/docfx.console.*").Single().Combine("tools"));
});

Task("Pack")
    .Description("Create the NuGet package")
    .IsDependentOn("Clean")
    .IsDependentOn("Build-API")
    .Does(() =>
{
    // We can't use .NET Core to pack because it doesn't support
    // targets from .NET Framework 3.5:
    // https://github.com/Microsoft/msbuild/issues/1333

    // We can't use NuGet.exe to pack because it doesn't support
    // .NET Core targets
    // https://github.com/NuGet/Home/issues/5832

    MSBuild("src/Connector.sln", configurator => configurator
        .SetConfiguration(configuration)
        .SetVerbosity(Verbosity.Minimal)
        .WithProperty("PackageOutputPath", new[] { "../../artifacts" })
        .WithProperty("IncludeSymbols", new[] { "true" })
        .WithTarget("Pack")
    );
});

Task("Deploy")
    .Description("Deploy the NuGet packages to the server")
    .IsDependentOn("Pack")
    .Does(() =>
{
    // We don't need to manually publish the symbol packages.
    var packages = GetFiles("artifacts/*.nupkg")
        .Where(f => !f.GetFilename().ToString().Contains(".symbols."))
        .Single()
        .ToString();

    var settings = new DotNetCoreNuGetPushSettings {
        Source = "https://api.nuget.org/v3/index.json",
        ApiKey = Environment.GetEnvironmentVariable("NUGET_KEY"),
    };
    DotNetCoreNuGetPush(packages, settings);
});

Task("Generate-DocWeb")
    .Description("Generate a static web with the documentation")
    .IsDependentOn("Build-API")
    .IsDependentOn("Fix-DocFx")
    .Does(() =>
{
    DocFxMetadata("docs/docfx.json");
    DocFxBuild("docs/docfx.json");
});

Task("Generate-DocPdf")
    .Description("Generate a PDF with the documentation")
    .IsDependentOn("Build-API")
    .IsDependentOn("Fix-DocFx")
    .Does(() =>
{
    DocFxMetadata("docs/docfx.json");
    DocFxPdf("docs/docfx.json");
});

Task("Update-DocRepo")
    .Description("Commit and push the latest documentation to the repository")
    .IsDependentOn("Generate-DocWeb")
    .Does(() =>
{
   int retcode;

    // Clone or pull
    var repo_doc = Directory("docs/repo");
    if (!DirectoryExists(repo_doc)) {
        retcode = StartProcess(
            "git",
            $"clone git@github.com:rticommunity/rticonnextdds-connector-cs {repo_doc} -b gh-pages");
        if (retcode != 0) {
            throw new Exception("Cannot clone repository");
        }
    } else {
        retcode = StartProcess("git", new ProcessSettings {
            Arguments = "pull",
            WorkingDirectory = repo_doc
        });
        if (retcode != 0) {
            throw new Exception("Cannot pull repository");
        }
    }

    // Copy the content of the web
    CopyDirectory("docs/_site", repo_doc);

    // Commit and push
    retcode = StartProcess("git", new ProcessSettings {
        Arguments = "add --all",
        WorkingDirectory = repo_doc
    });
    if (retcode != 0) {
        throw new Exception("Cannot add");
    }

    retcode = StartProcess("git", new ProcessSettings {
        Arguments = "commit -m \":books: Update doc from cake\"",
        WorkingDirectory = repo_doc
    });
    if (retcode != 0) {
        throw new Exception("Cannot commit doc repo");
    }

    retcode = StartProcess("git", new ProcessSettings {
        Arguments = "push origin gh-pages",
        WorkingDirectory = repo_doc
    });
    if (retcode != 0) {
        throw new Exception("Cannot push doc repo");
    }
});

Task("Default")
    .IsDependentOn("Build-API")
    .IsDependentOn("Build-Examples")
    .IsDependentOn("Run-UnitTests")
    .IsDependentOn("Run-AltCover");

Task("Travis")
    .IsDependentOn("Build-API")
    .IsDependentOn("Build-Examples")
    .IsDependentOn("Run-UnitTests")
    .IsDependentOn("Run-Linter-Gendarme")
    .IsDependentOn("Run-AltCover")
    .IsDependentOn("Generate-DocWeb");  // Validate documentation but don't update

RunTarget(target);


public void ReportWarning(string msg)
{
    if (warningAsErrors) {
        throw new Exception(msg);
    } else {
        Warning(msg);
    }
}

public string GetLoadLibraryEnvVar()
{
    if (IsRunningOnWindows()) {
        return "PATH";
    } else if (IsRunningOnMacOSX()) {
        return "DYLD_LIBRARY_PATH";
    } else if (IsRunningOnUnix()) {
        return "LD_LIBRARY_PATH";
    }

    throw new Exception("Unsupported platform");
}

public string GetNativeLibraryPath(bool x86 = false)
{
    string arch;
    if (IsRunningOnWindows()) {
        arch = x86 ? "i86Win32VS2010" : "x64Win64VS2013";
    } else if (IsRunningOnMacOSX()) {
        if (x86) {
            throw new Exception("32-bits not supported on MacOSX");
        }

        arch = "x64Darwin16clang8.0";
    } else if (IsRunningOnUnix()) {
        arch = x86 ? "i86Linux3.xgcc4.6.3" : "x64Linux2.6gcc4.4.5";
    } else {
        throw new Exception("Unsupported platform");
    }

    return MakeAbsolute(Directory($"rticonnextdds-connector/lib/{arch}")).FullPath;
}

[DllImport("libc")]
static extern int uname(IntPtr buf);

public bool IsRunningOnMacOSX()
{
    bool isMac = false;
    if (Environment.OSVersion.Platform == PlatformID.Unix) {
        IntPtr buf = Marshal.AllocHGlobal(8192);
        try {
            if (uname(buf) == 0) {
                var unameText = Marshal.PtrToStringAnsi(buf);
                isMac = (unameText == "Darwin");
            }
        } finally {
            Marshal.FreeHGlobal(buf);
        }
    }

    return isMac;
}

public void RunGendarme(string gendarme, string assembly, string ignore)
{
    var retcode = StartProcess(gendarme, $"--ignore {ignore} {assembly}");
    if (retcode != 0) {
        throw new Exception($"Gendarme found errors on {assembly}");
    }
}

public void TestWithAltCover(string projectPath, string assembly, string outputXml)
{
    string inputDir = $"{projectPath}/bin/{configuration}/net45";
    string outputDir = $"{inputDir}/__Instrumented";
    if (DirectoryExists(outputDir)) {
        DeleteDirectory(
            outputDir,
            new DeleteDirectorySettings { Recursive = true });
    }

    var altcoverArgs = new AltCover.Parameters.Primitive.PrepareArgs {
        InputDirectory = inputDir,
        OutputDirectory = outputDir,
        AssemblyFilter = new[] {
            "nunit.framework",
            "NUnit3",
            "RTI.Connext.Connector.UnitTests" },
        XmlReport = outputXml,
        OpenCover = true
    };
    Prepare(altcoverArgs);

    var nunitSettings = new NUnit3Settings {
        EnvironmentVariables = new Dictionary<string, string> {
            { GetLoadLibraryEnvVar(), GetNativeLibraryPath() }
        },
        NoResults = true
    };
    NUnit3($"{outputDir}/{assembly}", nunitSettings);
}
