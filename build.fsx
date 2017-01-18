#I @"src/packages/FAKE/tools"
#r "FakeLib.dll"
#r "System.Xml.Linq"

open System
open System.IO
open System.Text
open Fake
open Fake.FileUtils
open Fake.TaskRunnerHelper
open Fake.ProcessHelper
open Fake.DotNetCli
open Fake.EnvironmentHelper

cd __SOURCE_DIRECTORY__

//--------------------------------------------------------------------------------
// Information about the project for Nuget and Assembly info files
//--------------------------------------------------------------------------------


let product = "Akka.NET"
let authors = [ "Akka.NET Team" ]
let copyright = "Copyright © 2013-2016 Akka.NET Team"
let company = "Akka.NET Team"
let description = "Akka.NET is a port of the popular Java/Scala framework Akka to .NET"
let tags = ["akka";"actors";"actor";"model";"Akka";"concurrency"]
let configuration = "Release"

let toolDir = "tools"
let CloudCopyDir = toolDir @@ "CloudCopy"
let AzCopyDir = toolDir @@ "AzCopy"

// Read release notes and version

let parsedRelease =
    File.ReadLines "RELEASE_NOTES.md"
    |> ReleaseNotesHelper.parseReleaseNotes

let envBuildNumber = System.Environment.GetEnvironmentVariable("APPVEYOR_BUILD_NUMBER")
let buildNumber = if String.IsNullOrWhiteSpace(envBuildNumber) then "0" else envBuildNumber

let version = parsedRelease.AssemblyVersion + "." + buildNumber
let preReleaseVersion = version + "-alpha"
let versionSuffix = "alpha" + envBuildNumber

let isUnstableDocs = hasBuildParam "unstable"
let isPreRelease = hasBuildParam "nugetprerelease"
let release = if isPreRelease then ReleaseNotesHelper.ReleaseNotes.New(version, version + "-beta", parsedRelease.Notes) else parsedRelease

printfn "Assembly version: %s\nNuget version; %s\n" release.AssemblyVersion release.NugetVersion
//--------------------------------------------------------------------------------
// Directories

let binDir = "bin"
let testOutput = FullName "TestResults"
let perfOutput = FullName "PerfResults"

let nugetDir = binDir @@ "nuget"
let workingDir = binDir @@ "build"
let libDir = workingDir @@ @"lib\net45\"
let nugetExe = FullName @"src\.nuget\NuGet.exe"
let docDir = "bin" @@ "doc"
let sourceBrowserDocsDir = binDir @@ "sourcebrowser"
let msdeployPath = "C:\Program Files (x86)\IIS\Microsoft Web Deploy V3\msdeploy.exe"

//--------------------------------------------------------------------------------
// Restore packages

Target "RestorePackages" (fun _ -> 
    let solutions = !! "src/*.sln"

    let runSingleSolution project =
        DotNetCli.Restore
            (fun p -> 
                { p with
                    Project = project
                    NoCache = false })


    solutions |> Seq.iter (runSingleSolution)
)

//--------------------------------------------------------------------------------
// Clean build results

Target "Clean" <| fun _ ->
    CleanDir binDir
    CleanDir testOutput
    CleanDirs !! "./**/bin/Release"
    CleanDirs !! "./**/obj/Release"

//--------------------------------------------------------------------------------
// Generate AssemblyInfo files with the version for release notes 

open AssemblyInfoFile

Target "AssemblyInfo" <| fun _ ->
    CreateCSharpAssemblyInfoWithConfig "src/SharedAssemblyInfo.cs" [
        Attribute.Company company
        Attribute.Copyright copyright
        Attribute.Trademark ""
        Attribute.Version version
        Attribute.FileVersion version ] <| AssemblyInfoFileConfig(false)

    for file in !! "src/**/AssemblyInfo.fs" do
        let title =
            file
            |> Path.GetDirectoryName
            |> Path.GetDirectoryName
            |> Path.GetFileName

        CreateFSharpAssemblyInfo file [ 
            Attribute.Title title
            Attribute.Product product
            Attribute.Description description
            Attribute.Copyright copyright
            Attribute.Company company
            Attribute.ComVisible false
            Attribute.CLSCompliant true
            Attribute.Version version
            Attribute.FileVersion version ]


//--------------------------------------------------------------------------------
// Build the solution

Target "Build" <| fun _ ->
    // temporary disable on Unix
    if (isUnix) then
        let projects = !!   "src/**/Akka/Akka.csproj" ++
                            "src/**/Akka.Persistence/Akka.Persistence.csproj" ++
                            "src/**/Akka.Persistence.Query/Akka.Persistence.Query.csproj" ++
                            "src/**/Akka.Persistence.TestKit/Akka.Persistence.TestKit.csproj" ++
                            "src/**/Akka.Streams/Akka.Streams.csproj" ++
                            "src/**/Akka.Streams.TestKit/Akka.Streams.TestKit.csproj" ++
                            "src/**/Akka.TestKit/Akka.TestKit.csproj" ++
                            "src/contrib/**/Akka.Persistence.Query.Sql/Akka.Persistence.Query.Sql.csproj" ++
                            "src/contrib/**/Akka.Persistence.Sql.Common/Akka.Persistence.Sql.Common.csproj" ++
                            "src/contrib/**/Akka.TestKit.Xunit2/Akka.TestKit.Xunit2.csproj"

        let runSingleProject project =
            DotNetCli.Build
                (fun p -> 
                    { p with
                        Project = project
                        Configuration = configuration
                        Framework = "netstandard1.6" })

        projects |> Seq.iter (runSingleProject)

        let testProjects =   !! "src/**/Akka.Tests/Akka.Tests.csproj" ++
                                "src/**/Akka.Persistence.Tests/Akka.Persistence.Tests.csproj" ++
                                "src/**/Akka.Persistence.TestKit.Tests/Akka.Persistence.TestKit.Tests.csproj" ++
                                "src/**/Akka.Streams.Tests/Akka.Streams.Tests.csproj" ++
                                "src/**/Akka.TestKit.Tests/Akka.TestKit.Tests.csproj"

        let runTestProject project =
            DotNetCli.Build
                (fun p -> 
                    { p with
                        Project = project
                        Configuration = configuration
                        Framework = "netcoreapp1.0" })

        testProjects |> Seq.iter (runTestProject)

    else
        let projects = !! "src/core/**/*.csproj" ++
                        "src/contrib/cluster/**/*.csproj" ++
                        "src/contrib/persistence/**/*.csproj" -- 
                        "src/**/*.Tests.csproj"

        let runSingleProject project =
            DotNetCli.Build
                (fun p -> 
                    { p with
                        Project = project
                        Configuration = configuration})

        projects |> Seq.iter (runSingleProject)

//--------------------------------------------------------------------------------
// Build the docs
Target "Docs" <| fun _ ->
    !! "documentation/akkadoc.shfbproj"
    |> MSBuildRelease "" "Rebuild"
    |> ignore

//--------------------------------------------------------------------------------
// Push DOCs content to Windows Azure blob storage
Target "AzureDocsDeploy" (fun _ ->
    let rec pushToAzure docDir azureUrl container azureKey trialsLeft =
        let tracing = enableProcessTracing
        enableProcessTracing <- false
        let arguments = sprintf "/Source:%s /Dest:%s /DestKey:%s /S /Y /SetContentType" (Path.GetFullPath docDir) (azureUrl @@ container) azureKey
        tracefn "Pushing docs to %s. Attempts left: %d" (azureUrl) trialsLeft
        try 
            
            let result = ExecProcess(fun info ->
                info.FileName <- AzCopyDir @@ "AzCopy.exe"
                info.Arguments <- arguments) (TimeSpan.FromMinutes 120.0) //takes a very long time to upload
            if result <> 0 then failwithf "Error during AzCopy.exe upload to azure."
        with exn -> 
            if (trialsLeft > 0) then (pushToAzure docDir azureUrl container azureKey (trialsLeft-1))
            else raise exn
    let canPush = hasBuildParam "azureKey" && hasBuildParam "azureUrl"
    if (canPush) then
         printfn "Uploading API docs to Azure..."
         let azureUrl = getBuildParam "azureUrl"
         let azureKey = (getBuildParam "azureKey") + "==" //hack, because it looks like FAKE arg parsing chops off the "==" that gets tacked onto the end of each Azure storage key
         if(isUnstableDocs) then
            pushToAzure docDir azureUrl "unstable" azureKey 3
         if(not isUnstableDocs) then
            pushToAzure docDir azureUrl "stable" azureKey 3
            pushToAzure docDir azureUrl release.NugetVersion azureKey 3
    if(not canPush) then
        printfn "Missing required paraments to push docs to Azure. Run build HelpDocs to find out!"
            
)

Target "PublishDocs" DoNothing

//--------------------------------------------------------------------------------
// Copy the build output to bin directory
//--------------------------------------------------------------------------------

Target "CopyOutput" <| fun _ ->
    
    let copyOutput project =
        let src = "src" @@ project @@ @"bin/Release/"
        let dst = binDir @@ project
        CopyDir dst src allFiles
    [ "core/Akka"
      "core/Akka.FSharp"
      "core/Akka.TestKit"
      "core/Akka.Remote"
      "core/Akka.Remote.TestKit"
      "core/Akka.Cluster"
      "core/Akka.Cluster.TestKit"
      "core/Akka.MultiNodeTestRunner"
      "core/Akka.Persistence"
      "core/Akka.Persistence.FSharp"
      "core/Akka.Persistence.TestKit"
      "core/Akka.Persistence.Query"
      "core/Akka.Streams"
      "core/Akka.Streams.TestKit"
      "contrib/dependencyinjection/Akka.DI.Core"
      "contrib/dependencyinjection/Akka.DI.TestKit"
      "contrib/testkits/Akka.TestKit.Xunit" 
      "contrib/testkits/Akka.TestKit.Xunit2" 
      "contrib/serializers/Akka.Serialization.Wire" 
      "contrib/cluster/Akka.Cluster.Tools"
      "contrib/cluster/Akka.Cluster.Sharding"
      ]
    |> List.iter copyOutput

Target "BuildRelease" DoNothing



//--------------------------------------------------------------------------------
// Tests targets
//--------------------------------------------------------------------------------

//--------------------------------------------------------------------------------
// Run tests

Target "RunTests" <| fun _ ->  
    mkdir testOutput
    let testFramework = if isUnix then "netcoreapp1.0" else ""

    let testProjects =   !! "src/**/Akka.Tests/Akka.Tests.csproj" ++
                            "src/**/Akka.Persistence.Tests/Akka.Persistence.Tests.csproj" ++
                            "src/**/Akka.Persistence.TestKit.Tests/Akka.Persistence.TestKit.Tests.csproj" ++
                            "src/**/Akka.Streams.Tests/Akka.Streams.Tests.csproj" ++
                            "src/**/Akka.TestKit.Tests/Akka.TestKit.Tests.csproj"

    let runSingleProject project =
        let projectName = new DirectoryInfo(project);
        DotNetCli.Test
            (fun p -> 
                { p with
                    Project = project
                    Framework = testFramework })

    testProjects |> Seq.iter (runSingleProject)

(* Debug helper for troubleshooting an issue we had when we were running multi-node tests multiple times *)
Target "PrintMultiNodeTests" <| fun _ ->
    let testSearchPath =
        let assemblyFilter = getBuildParamOrDefault "spec-assembly" String.Empty
        sprintf "src/**/bin/Release/*%s*.Tests.MultiNode.dll" assemblyFilter
    (!! testSearchPath) |> Seq.iter (printfn "%s")
    


Target "MultiNodeTests" <| fun _ ->
    let testSearchPath =
        let assemblyFilter = getBuildParamOrDefault "spec-assembly" String.Empty
        sprintf "src/**/bin/Release/*%s*.Tests.MultiNode.dll" assemblyFilter

    mkdir testOutput
    let multiNodeTestPath = findToolInSubPath "Akka.MultiNodeTestRunner.exe" "bin/core/Akka.MultiNodeTestRunner*"
    let multiNodeTestAssemblies = !! testSearchPath
    printfn "Using MultiNodeTestRunner: %s" multiNodeTestPath

    let runMultiNodeSpec assembly =
        let spec = getBuildParam "spec"

        let args = new StringBuilder()
                |> append assembly
                |> append "-Dmultinode.enable-filesink=on"
                |> append (sprintf "-Dmultinode.output-directory=\"%s\"" testOutput)
                |> appendIfNotNullOrEmpty spec "-Dmultinode.test-spec="
                |> toText

        let result = ExecProcess(fun info -> 
            info.FileName <- multiNodeTestPath
            info.WorkingDirectory <- (Path.GetDirectoryName (FullName multiNodeTestPath))
            info.Arguments <- args) (System.TimeSpan.FromMinutes 60.0) (* This is a VERY long running task. *)
        if result <> 0 then failwithf "MultiNodeTestRunner failed. %s %s" multiNodeTestPath args
    
    multiNodeTestAssemblies |> Seq.iter (runMultiNodeSpec)

//--------------------------------------------------------------------------------
// NBench targets 
//--------------------------------------------------------------------------------
Target "NBench" <| fun _ ->
    let testSearchPath =
        let assemblyFilter = getBuildParamOrDefault "spec-assembly" String.Empty
        sprintf "src/**/bin/Release/*%s*.Tests.Performance.dll" assemblyFilter

    mkdir perfOutput
    let nbenchTestPath = findToolInSubPath "NBench.Runner.exe" "src/packages/NBench.Runner*"
    let nbenchTestAssemblies = !! testSearchPath
    printfn "Using NBench.Runner: %s" nbenchTestPath

    let runNBench assembly =
        let spec = getBuildParam "spec"

        let args = new StringBuilder()
                |> append assembly
                |> append (sprintf "output-directory=\"%s\"" perfOutput)
                |> append (sprintf "concurrent=\"%b\"" true)
                |> append (sprintf "trace=\"%b\"" true)
                |> toText

        let result = ExecProcess(fun info -> 
            info.FileName <- nbenchTestPath
            info.WorkingDirectory <- (Path.GetDirectoryName (FullName nbenchTestPath))
            info.Arguments <- args) (System.TimeSpan.FromMinutes 15.0) (* Reasonably long-running task. *)
        if result <> 0 then failwithf "NBench.Runner failed. %s %s" nbenchTestPath args
    
    nbenchTestAssemblies |> Seq.iter (runNBench)

//--------------------------------------------------------------------------------
// Clean NBench output
Target "CleanPerf" <| fun _ ->
    DeleteDir perfOutput


//--------------------------------------------------------------------------------
// Nuget targets 
//--------------------------------------------------------------------------------

module Nuget = 
    // add Akka dependency for other projects
    let getAkkaDependency project =
        match project with
        | "Akka" -> []
        | "Akka.Cluster" -> ["Akka.Remote", release.NugetVersion]
        | "Akka.Cluster.TestKit" -> ["Akka.Remote.TestKit", release.NugetVersion; "Akka.Cluster", release.NugetVersion]
        | "Akka.Cluster.Sharding" -> ["Akka.Cluster.Tools", preReleaseVersion; "Akka.Persistence", preReleaseVersion]
        | "Akka.Cluster.Tools" -> ["Akka.Cluster", release.NugetVersion]
        | "Akka.MultiNodeTestRunner" -> [] // all binaries for the multinodetest runner have to be included locally
        | "Akka.Persistence.TestKit" -> ["Akka.Persistence", preReleaseVersion; "Akka.TestKit.Xunit2", release.NugetVersion]
        | "Akka.Persistence.Query" -> ["Akka.Persistence", preReleaseVersion; "Akka.Streams", preReleaseVersion]
        | "Akka.Persistence.Query.Sql" -> ["Akka.Persistence.Query", preReleaseVersion; "Akka.Persistence.Sql.Common", preReleaseVersion]
        | "Akka.Persistence.Sql.TestKit" -> ["Akka.Persistence.Query.Sql", preReleaseVersion; "Akka.Persistence.TestKit", preReleaseVersion; "Akka.Streams.TestKit", preReleaseVersion]
        | persistence when (persistence.Contains("Sql") && not (persistence.Equals("Akka.Persistence.Sql.Common"))) -> ["Akka.Persistence.Sql.Common", preReleaseVersion]
        | persistence when (persistence.StartsWith("Akka.Persistence.")) -> ["Akka.Persistence", preReleaseVersion]
        | "Akka.DI.TestKit" -> ["Akka.DI.Core", release.NugetVersion; "Akka.TestKit.Xunit2", release.NugetVersion]
        | testkit when testkit.StartsWith("Akka.TestKit.") -> ["Akka.TestKit", release.NugetVersion]
        | "Akka.Remote.TestKit" -> ["Akka.Remote", release.NugetVersion; "Akka.TestKit.Xunit2", release.NugetVersion;]
        | "Akka.Streams" -> ["Akka", release.NugetVersion]
        | "Akka.Streams.TestKit" -> ["Akka.Streams", preReleaseVersion; "Akka.TestKit", release.NugetVersion]
        | _ -> ["Akka", release.NugetVersion]

    // used to add -pre suffix to pre-release packages
    let getProjectVersion project =
      match project with
      | "Akka.Serialization.Wire" -> preReleaseVersion
      | cluster when (cluster.StartsWith("Akka.Cluster.") && not (cluster.EndsWith("TestKit"))) -> preReleaseVersion
      | persistence when persistence.StartsWith("Akka.Persistence") -> preReleaseVersion
      | streams when streams.StartsWith("Akka.Streams") -> preReleaseVersion
      | _ -> release.NugetVersion

open Nuget

//--------------------------------------------------------------------------------
// Pack nuget for all projects
// Publish to nuget.org if nugetkey is specified

let createNugetPackages _ =
    ensureDirectory nugetDir
    let outputPath = __SOURCE_DIRECTORY__ + @"\" + nugetDir

    let projects = !!   "src/**/Akka.csproj" ++
                        "src/**/Akka.Cluster.csproj" ++
                        "src/**/Akka.Cluster.TestKit.csproj" ++
                        "src/**/Akka.Persistence.csproj" ++
                        "src/**/Akka.Persistence.Query.csproj" ++
                        "src/**/Akka.Persistence.TestKit.csproj" ++
                        "src/**/Akka.Remote.csproj" ++
                        "src/**/Akka.Remote.TestKit.csproj" ++
                        "src/**/Akka.Streams.csproj" ++
                        "src/**/Akka.Streams.TestKit.csproj" ++
                        "src/**/Akka.TestKit.csproj" ++
                        "src/contrib/**/Akka.Persistence.Query.Sql.csproj" ++
                        "src/contrib/**/Akka.Persistence.Sql.Common.csproj" ++
                        "src/contrib/**/Akka.TestKit.Xunit2.csproj"

    let runSingleProject project =
        DotNetCli.Pack
            (fun p -> 
                { p with
                    Project = project
                    Configuration = configuration
                    VersionSuffix = versionSuffix
                    AdditionalArgs = ["--include-symbols"]
                    OutputPath = outputPath })

    projects |> Seq.iter (runSingleProject)

let publishNugetPackages _ = 
    let rec publishPackage url accessKey trialsLeft packageFile =
        let tracing = enableProcessTracing
        enableProcessTracing <- false
        let args p =
            match p with
            | (pack, key, "") -> sprintf "push \"%s\" %s" pack key
            | (pack, key, url) -> sprintf "push \"%s\" %s -source %s" pack key url

        tracefn "Pushing %s Attempts left: %d" (FullName packageFile) trialsLeft
        try 
            let result = ExecProcess (fun info -> 
                    info.FileName <- nugetExe
                    info.WorkingDirectory <- (Path.GetDirectoryName (FullName packageFile))
                    info.Arguments <- args (packageFile, accessKey,url)) (System.TimeSpan.FromMinutes 1.0)
            enableProcessTracing <- tracing
            if result <> 0 then failwithf "Error during NuGet symbol push. %s %s" nugetExe (args (packageFile, "key omitted",url))
        with exn -> 
            if (trialsLeft > 0) then (publishPackage url accessKey (trialsLeft-1) packageFile)
            else raise exn
    let shouldPushNugetPackages = hasBuildParam "nugetkey"
    let shouldPushSymbolsPackages = (hasBuildParam "symbolspublishurl") && (hasBuildParam "symbolskey")
    
    if (shouldPushNugetPackages || shouldPushSymbolsPackages) then
        printfn "Pushing nuget packages"
        if shouldPushNugetPackages then
            let normalPackages= 
                !! (nugetDir @@ "*.nupkg") 
                -- (nugetDir @@ "*.symbols.nupkg") |> Seq.sortBy(fun x -> x.ToLower())
            for package in normalPackages do
                try
                    publishPackage (getBuildParamOrDefault "nugetpublishurl" "") (getBuildParam "nugetkey") 3 package
                with exn ->
                    printfn "%s" exn.Message

        if shouldPushSymbolsPackages then
            let symbolPackages= !! (nugetDir @@ "*.symbols.nupkg") |> Seq.sortBy(fun x -> x.ToLower())
            for package in symbolPackages do
                try
                    publishPackage (getBuildParam "symbolspublishurl") (getBuildParam "symbolskey") 3 package
                with exn ->
                    printfn "%s" exn.Message


Target "Nuget" <| fun _ -> 
    createNugetPackages()
    publishNugetPackages()

Target "CreateNuget" <| fun _ -> 
    createNugetPackages()

Target "PublishNuget" <| fun _ -> 
    publishNugetPackages()



//--------------------------------------------------------------------------------
// Help 
//--------------------------------------------------------------------------------

Target "Help" <| fun _ ->
    List.iter printfn [
      "usage:"
      "build [target]"
      ""
      " Targets for building:"
      " * Build      Builds"
      " * Nuget      Create and optionally publish nugets packages"
      " * RunTests   Runs tests"
      " * MultiNodeTests  Runs the slower multiple node specifications"
      " * All        Builds, run tests, creates and optionally publish nuget packages"
      ""
      " Other Targets"
      " * Help       Display this help" 
      " * HelpNuget  Display help about creating and pushing nuget packages" 
      " * HelpDocs   Display help about creating and pushing API docs"
      " * HelpMultiNodeTests  Display help about running the multiple node specifications"
      ""]

Target "HelpNuget" <| fun _ ->
    List.iter printfn [
      "usage: "
      "build Nuget [nugetkey=<key> [nugetpublishurl=<url>]] "
      "            [symbolskey=<key> symbolspublishurl=<url>] "
      "            [nugetprerelease=<prefix>]"
      ""
      "Arguments for Nuget target:"
      "   nugetprerelease=<prefix>   Creates a pre-release package."
      "                              The version will be version-prefix<date>"
      "                              Example: nugetprerelease=dev =>"
      "                                       0.6.3-dev1408191917"
      ""
      "In order to publish a nuget package, keys must be specified."
      "If a key is not specified the nuget packages will only be created on disk"
      "After a build you can find them in bin/nuget"
      ""
      "For pushing nuget packages to nuget.org and symbols to symbolsource.org"
      "you need to specify nugetkey=<key>"
      "   build Nuget nugetKey=<key for nuget.org>"
      ""
      "For pushing the ordinary nuget packages to another place than nuget.org specify the url"
      "  nugetkey=<key>  nugetpublishurl=<url>  "
      ""
      "For pushing symbols packages specify:"
      "  symbolskey=<key>  symbolspublishurl=<url> "
      ""
      "Examples:"
      "  build Nuget                      Build nuget packages to the bin/nuget folder"
      ""
      "  build Nuget nugetprerelease=dev  Build pre-release nuget packages"
      ""
      "  build Nuget nugetkey=123         Build and publish to nuget.org and symbolsource.org"
      ""
      "  build Nuget nugetprerelease=dev nugetkey=123 nugetpublishurl=http://abc"
      "              symbolskey=456 symbolspublishurl=http://xyz"
      "                                   Build and publish pre-release nuget packages to http://abc"
      "                                   and symbols packages to http://xyz"
      ""]

Target "HelpDocs" <| fun _ ->
    List.iter printfn [
      "usage: "
      "build Docs"
      "Just builds the API docs for Akka.NET locally. Does not attempt to publish."
      ""
      "build PublishDocs azureKey=<key> "
      "                  azureUrl=<url> "
      "                 [unstable=true]"
      ""
      "Arguments for PublishDocs target:"
      "   azureKey=<key>             Azure blob storage key."
      "                              Used to authenticate to the storage account."
      ""
      "   azureUrl=<url>             Base URL for Azure storage container."
      "                              FAKE will automatically set container"
      "                              names based on build parameters."
      ""
      "   [unstable=true]            Indicates that we'll publish to an Azure"
      "                              container named 'unstable'. If this param"
      "                              is not present we'll publish to containers"
      "                              'stable' and the 'release.version'"
      ""
      "In order to publish documentation all of these values must be provided."
      "Examples:"
      "  build PublishDocs azureKey=1s9HSAHA+..."
      "                    azureUrl=http://fooaccount.blob.core.windows.net/docs"
      "                                   Build and publish docs to http://fooaccount.blob.core.windows.net/docs/stable"
      "                                   and http://fooaccount.blob.core.windows.net/docs/{release.version}"
      ""
      "  build PublishDocs azureKey=1s9HSAHA+..."
      "                    azureUrl=http://fooaccount.blob.core.windows.net/docs"
      "                    unstable=true"
      "                                   Build and publish docs to http://fooaccount.blob.core.windows.net/docs/unstable"
      ""]


Target "HelpMultiNodeTests" <| fun _ ->
    List.iter printfn [
      "usage: "
      "build MultiNodeTests [spec-assembly=<filter>]"
      "Just runs the MultiNodeTests. Does not build the projects."
      ""
      "Arguments for MultiNodeTests target:"
      "   [spec-assembly=<filter>]  Restrict which spec projects are run."
      ""
      "       Alters the discovery filter to enable restricting which specs are run."
      "       If not supplied the filter used is '*.Tests.Multinode.Dll'"
      "       When supplied this is altered to '*<filter>*.Tests.Multinode.Dll'"
      ""]
//--------------------------------------------------------------------------------
//  Target dependencies
//--------------------------------------------------------------------------------

// build dependencies
"Clean" ==> "AssemblyInfo" ==> "RestorePackages" ==> "Build" ==> "CopyOutput" ==> "BuildRelease"

// tests dependencies
"Clean" ==> "RunTests"
"Clean" ==> "MultiNodeTests"

// NBench dependencies
"Clean" ==> "NBench"

// nuget dependencies
"Clean" ==> "CreateNuget"
"Clean" ==> "BuildRelease" ==> "Nuget"

//docs dependencies
"BuildRelease" ==> "Docs" ==> "AzureDocsDeploy" ==> "PublishDocs"

Target "All" DoNothing
"BuildRelease" ==> "All"
"RunTests" ==> "All"
"MultiNodeTests" ==> "All"
"NBench" ==> "All"
"Nuget" ==> "All"

Target "AllTests" DoNothing //used for Mono builds, due to Mono 4.0 bug with FAKE / NuGet https://github.com/fsharp/fsharp/issues/427
"BuildRelease" ==> "AllTests"
"RunTests" ==> "AllTests"
"MultiNodeTests" ==> "AllTests"

RunTargetOrDefault "Help"