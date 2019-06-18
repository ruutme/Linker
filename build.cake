#load build/paths.cake
#load build/version.cake
#load build/package.cake
#load build/urls.cake

#tool nuget:?package=GitVersion.CommandLine&version=4.0.0-beta0012
#tool nuget:?package=OctopusTools&version=6.7.0

#addin nuget:?package=Cake.Npm&version=0.17.0
#addin nuget:?package=Cake.Curl&version=4.1.0
//#addin nuget:?package=Cake.Coverlet&
//#tool nuget:?package=Cake.Curl

var target = Argument("Target", "Build");
Setup<PackageMetadata> (context => {
    return new PackageMetadata(
        outputDirectory: Argument("packageOutputDirectory", "packages"),
        name: "Linker-7"
    );
});
//Teardown<TState> (CakeContext context, TState state => {});
Task("Compile")
    .Does(() =>
{
    DotNetCoreBuild(Paths.SolutionFile.FullPath);
    
    //Information("Hello");
    //Context.Log.Information("Hello");
    //Context.Environment.GetEnvironmentVariable("");
});

Task("Test")
    .IsDependentOn("Compile")
    .Does(() =>
{

    DotNetCoreTest(
        Paths.TestProjectFile.FullPath,
        new DotNetCoreTestSettings {
            Logger = "trx", // VSTest results format
            ResultsDirectory = Paths.TestResultDirectory 
        }
    );
});

Task("Version")
    .Does<PackageMetadata>(package =>
{
  // What if we want to explicitly commit a tag, tag is signaling you are releasing
  // Manual way instead of GitVersion
  package.Version = ReadVersionFromProjectFile(Context);
  if (package.Version == null)
  {
      Information ("Project version missing, falling back to GitVersion");
      package.Version = GitVersion().FullSemVer;
  }
  // AssemblyInfo

  Information($"Determined version number {package.Version}");
});

Task("Build-Frontend")
    .Does(() =>
{
    NpmInstall(settings => settings.FromPath(Paths.FrontEndDirectory /*Paths.WebProjectFile.GetDirectory()*/));
    NpmRunScript("build", settings => settings.FromPath(Paths.FrontEndDirectory));
});

Task("Package-Zip")
    .IsDependentOn("Test")
    .IsDependentOn("Build-Frontend")
    .IsDependentOn("Version")
    .Does<PackageMetadata>(package =>
{
    // clean first output directory
    CleanDirectory(package.OutputDirectory);
    package.Extension = "zip";
    DotNetCorePublish(
        Paths.WebProjectFile.GetDirectory().FullPath,
        new DotNetCorePublishSettings {
            OutputDirectory = Paths.PublishDirectory,
            NoBuild = true,
            NoRestore = true,
            MSBuildSettings = new DotNetCoreMSBuildSettings {
                NoLogo = true
            }
        }
    );
    Zip(Paths.PublishDirectory, package.FullPath);
    
});
Task("Package-Octopus")
    .IsDependentOn("Test")
    .IsDependentOn("Build-Frontend")
    .IsDependentOn("Version")
    .Does<PackageMetadata>(package =>
{
    CleanDirectory(package.OutputDirectory);
    package.Extension = "nupkg";
    DotNetCorePublish(
        Paths.WebProjectFile.GetDirectory().FullPath,
        new DotNetCorePublishSettings {
            OutputDirectory = Paths.PublishDirectory,
            NoBuild = true,
            NoRestore = true,
            MSBuildSettings = new DotNetCoreMSBuildSettings {
                NoLogo = true
            }
        }
    );

    OctoPack(package.Name, new OctopusPackSettings {
        Format = OctopusPackFormat.NuPkg,
        Version = package.Version,
        BasePath = Paths.PublishDirectory,
        OutFolder = package.OutputDirectory
    });
});

Task("Deploy-Kudu")
    .Description("Deploys to Kudu using zip deployment feature")
    .IsDependentOn("Package-Zip")
    .Does<PackageMetadata>(package =>
{
    CurlUploadFile(
        package.FullPath,
        Urls.KuduDeployUrl,
        new CurlSettings {
            Username = EnvironmentVariable("deploymentUser"),
            Password = EnvironmentVariable("deploymentPassword"),
            RequestCommand = "POST",
            ProgressBar = true,
            ArgumentCustomization = args => args.Append("--fail")
        }
    );
});

Task("Deploy-Octopus")
    .IsDependentOn("Package-Octopus")
    .Does<PackageMetadata>(package =>
{
    // Upload package
    OctoPush(
        Urls.OctopusServerUrl.AbsoluteUri,
        EnvironmentVariable("OctopusApiKey"),
        package.FullPath,
        new OctopusPushSettings {
            EnableServiceMessages = true
        }
    );
    OctoCreateRelease(
        package.Name,
        new CreateReleaseSettings {
            Server = Urls.OctopusServerUrl.AbsoluteUri,
            ApiKey = EnvironmentVariable("OctopusApiKey"),
            ReleaseNumber = package.Version,
            DefaultPackageVersion = package.Version,
            DeployTo = "Test", // Could be script argument like target
            IgnoreExisting = true,
            DeploymentProgress = true,
            WaitForDeployment = true
        }
    );
});

Task("Set-Build-Number")
    .WithCriteria(() => BuildSystem.IsRunningOnTeamCity)
    .Does<PackageMetadata>(package =>
{
    var buildNumber = TFBuild.Environment.Build.Number;
    TFBuild.Commands.UpdateBuildNumber($"{package.Version}+{buildNumber}");   

    buildNumber = TeamCity.Environment.Build.Number;
    TeamCity.SetBuildNumber($"{package.Version}+{buildNumber}"); 
});

Task("Publish-Build-Artifact")
    .WithCriteria(() => BuildSystem.IsRunningOnTeamCity)
    .IsDependentOn("Package-Zip")
    .Does<PackageMetadata>(package =>
{
    //CleanDirectory(package.OutputDirectory); already done
    TFBuild.Commands.UploadArtifactDirectory(package.OutputDirectory);
    TeamCity.PublishArtifacts(package.FullPath);
    foreach (var p in GetFiles(package.OutputDirectory + "/*.zip")) {
        TeamCity.PublishArtifacts(p.FullPath);
    }
});

Task("Publish-Test-Results")
    .WithCriteria(() => BuildSystem.IsRunningOnTeamCity)
    .IsDependentOn("Test")
    .Does(() =>
{
    TFBuild.Commands.PublishTestResults(
        new TFBuildPublishTestResultsData {
            TestRunner = TFTestRunnerType.VSTest,
            TestResultsFiles = GetFiles(Paths.TestResultDirectory + "/*.trx").ToList()
        }
    );
    foreach(var testResult in GetFiles(Paths.TestResultDirectory + "/*.trx")){
        TeamCity.ImportData("vstest", testResult);
    }
});

Task("Publish-Code-Coverage-Results")
    .WithCriteria(() => BuildSystem.IsRunningOnTeamCity)
    .IsDependentOn("Test")
    .Does(() =>
{
    TFBuild.Commands.PublishCodeCoverage(
        new TFBuildPublishCodeCoverageData {
            //TestRunner = TFTestRunnerType.VSTest,
            //TestResultsFiles = GetFiles(Paths.TestResultDirectory + "/*.trx").ToList()
        }
    );
    /* 
    foreach(var testResult in GetFiles(Paths.TestResultDirectory + "/*.trx")){
        TeamCity.ImportData("vstest", testResult);
    }*/
});

Task("Build-CI")
    .IsDependentOn("Compile")
    .IsDependentOn("Test")
    .IsDependentOn("Build-Frontend")
    .IsDependentOn("Version")
    .IsDependentOn("Package-Zip")
    .IsDependentOn("Set-Build-Number")
    .IsDependentOn("Publish-Build-Artifact")
    .IsDependentOn("Publish-Test-Results");

RunTarget(target);