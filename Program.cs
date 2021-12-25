using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.IO;
using System.Diagnostics;
using Octokit;
using System.IO.Compression;

namespace JenkinsBuilder
{
    internal class GlobalConfig
    {
        public static string UE_PATH = "C:\\UnrealEngine\\UE_4.27\\";
        public static string UE_BUILD_TOOL = $"{UE_PATH}Engine\\Binaries\\DotNET\\UnrealBuildTool.exe";
        public static string UE_UAT_TOOL = $"{UE_PATH}Engine\\Build\\BatchFiles\\RunUAT.bat";

        public static string UE_PLUGIN_BUILD_COMMAND = "BuildPlugin -Plugin=\"%WORKSPACE%\\%PROJECT_NAME%.uplugin\" -Package=\"%WORKSPACE%\\Packaged\" -Rocket -VS2019 -TargetPlatforms=%TARGET_PLATFORM%";
        public static string UE_PROJECT_BUILD_COMMAND1 = "-projectfiles -project=\"%WORKSPACE%\\%PROJECT_NAME%.uproject\" -game -rocket -progress \"%ENGINE_PATH%%UE_BUILD_TOOL%\" %PROJECT_NAME% %BUILD_CONFIGURATION% %TARGET_PLATFORM% -project=\"%WORKSPACE%\\%PROJECT_NAME%.uproject\" -rocket -editorrecompile -progress -noubtmakefiles -NoHotReloadFromIDE -2019";
        public static string UE_PROJECT_BUILD_COMMAND2 = "BuildCookRun -project=\"%WORKSPACE%\\%PROJECT_NAME%.uproject\" -noP4 -platform=%TARGET_PLATFORM% -clientconfig=%BUILD_CONFIGURATION% -cook -numcookerstospawn=8 -compressed -EncryptIniFiles -ForDistribution -allmaps -build -stage -pak -prereqs -package -archive -archivedirectory=\"%WORKSPACE%\\Saved\\Builds\"";

        // UE_PLUGIN_BUILD_COMMAND = UE_UAT_TOOL
        // UE_PROJECT_BUILD_COMMAND1 = UE_BUILD_TOOL
        // UE_PROJECT_BUILD_COMMAND2 = UE_UAT_TOOL

        public static string ConfigFile = $"{System.Windows.Forms.Application.StartupPath}\\JenkinsBuilder.json";
        public static string GitHubTokenFile = $"{System.Windows.Forms.Application.StartupPath}\\GitHubToken.txt";
    }



    internal class Program
    {
        static string GitHubToken = "";

        // https://stackoverflow.com/a/57100143/8462069
        static string FormatJson(string json, string indent = "  ")
        {
            var indentation = 0;
            var quoteCount = 0;
            var escapeCount = 0;

            var result =
                from ch in json ?? string.Empty
                let escaped = (ch == '\\' ? escapeCount++ : escapeCount > 0 ? escapeCount-- : escapeCount) > 0
                let quotes = ch == '"' && !escaped ? quoteCount++ : quoteCount
                let unquoted = quotes % 2 == 0
                let colon = ch == ':' && unquoted ? ": " : null
                let nospace = char.IsWhiteSpace(ch) && unquoted ? string.Empty : null
                let lineBreak = ch == ',' && unquoted ? ch + Environment.NewLine + string.Concat(Enumerable.Repeat(indent, indentation)) : null
                let openChar = (ch == '{' || ch == '[') && unquoted ? ch + Environment.NewLine + string.Concat(Enumerable.Repeat(indent, ++indentation)) : ch.ToString()
                let closeChar = (ch == '}' || ch == ']') && unquoted ? Environment.NewLine + string.Concat(Enumerable.Repeat(indent, --indentation)) + ch : ch.ToString()
                select colon ?? nospace ?? lineBreak ?? (
                    openChar.Length > 1 ? openChar : closeChar
                );

            return string.Concat(result);
        }

        internal enum EStatus
        {
            Warning,
            Info,
            Error
        }

        static void Print(string message, EStatus status = EStatus.Info)
        {
            string StatusChar = status == EStatus.Warning ? "{!}" : status == EStatus.Info ? "{+}" : "{!!!}";

            string Message = $"{StatusChar} {message}";
            Console.Out.WriteLine(Message);
        }

        static void PrintError(string message)
        {
            Print(message, EStatus.Error);
            Environment.Exit(-1);
        }

        static void StartProcess(string command, string args)
        {
            var p = new Process();
            p.StartInfo = new ProcessStartInfo(command)
            {
                Arguments = $@"{args}",
                UseShellExecute = false,
                RedirectStandardError = true
            };
            p.Start();
            p.WaitForExit();
            int exitCode = p.ExitCode;

            if (exitCode != 0)
            {
                Environment.Exit(-1);
            }

            // TODO: If error then exit and print error
        }

        static bool IsRunning(string processName)
        {
            return Process.GetProcesses().Any(p => p.ProcessName.Contains(processName));
        }

        static void CheckUEProcesses()
        {
            int Tick = 0;
            while (IsRunning("AutomationTool") || IsRunning("UnrealBuildTool"))
            {
                if (Tick >= 60)
                {
                    Print("We've been waiting for more than 10 minutes. Perhaps there's an issue?", EStatus.Warning);
                }

                if (Tick % 2 == 0)
                {
                    Print("Waiting for AutomationTool or UnrealBuildTool to exit.");
                }

                Tick++;
                System.Threading.Thread.Sleep(10000);
            }
        }

        static void CleanUnrealProject(string WorkspaceDir)
        {

        }

        static string ParseCommand(string str, string ProjectName, string WorkspaceDir, string Platforms)
        {
            string command = str;
            command = command.Replace("%ENGINE_PATH%", GlobalConfig.UE_PATH);
            command = command.Replace("%PROJECT_NAME%", ProjectName);
            command = command.Replace("%WORKSPACE%", WorkspaceDir);
            command = command.Replace("%TARGET_PLATFORM%", Platforms);
            return command;
        }

        static async Task Build(dynamic Project, string WorkspaceDir, bool bShouldPublish = false)
        {
            Print("Building is starting.");

            bool bIsUEProject = false;
            bool bIsUEPlugin = false;
            string Platforms = "";

            string ProjectName = Project.Key.ToString();
            foreach (dynamic item in Project.Value)
            {
                string key = item.Key;

                if (key.ToString() == "UE")
                {
                    bIsUEProject = item.Value;
                    continue;
                }

                if (bIsUEProject)
                {
                    if (key.ToString() == "TargetPlatform")
                    {
                        Platforms = string.Join("+", item.Value);
                    }

                    if (key.ToString() == "UEPlugin")
                    {
                        bIsUEPlugin = item.Value;
                    }
                }
            }

            // if the project is an Unreal Engine project then we're going to want to check if some processes are running before kicking off a build
            if (bIsUEProject)
            {
                Print("Checking for Unreal Processes");
                CheckUEProcesses();

                Print("No Unreal Processes running");

                Print("Cleaning the workspace directory before building.");
                CleanUnrealProject(WorkspaceDir);
                Print("Finished cleaning workspace directory. Begin building.");

                if (bIsUEPlugin)
                {
                    string cmd = ParseCommand(GlobalConfig.UE_PLUGIN_BUILD_COMMAND, ProjectName, WorkspaceDir, Platforms);
                    Print($"Running command {cmd}");
                    StartProcess(GlobalConfig.UE_UAT_TOOL, cmd);
                }
                else
                {
                    string cmd = ParseCommand(GlobalConfig.UE_PROJECT_BUILD_COMMAND1, ProjectName, WorkspaceDir, Platforms);
                    Print($"Running command {cmd}");
                    StartProcess(GlobalConfig.UE_BUILD_TOOL, cmd);

                    CheckUEProcesses();

                    cmd = ParseCommand(GlobalConfig.UE_PROJECT_BUILD_COMMAND2, ProjectName, WorkspaceDir, Platforms);
                    Print($"Running command {cmd}");
                    StartProcess(GlobalConfig.UE_UAT_TOOL, cmd);
                }
            }

            if (bShouldPublish)
            {
                await Publish(Project, WorkspaceDir);
            }
        }

        static async Task Publish(dynamic Project, string WorkspaceDir, string BuildConfiguration = "Release")
        {
            Print("Publishing this build.");

            var client = new GitHubClient(new ProductHeaderValue("JenkinsBuilder"));
            var tokenAuth = new Credentials(GitHubToken);
            client.Credentials = tokenAuth;

            string ProjectName = Project.Key.ToString();
            string PackagedDir = "";
            string GitHubAuthor = "";
            bool bIsUEPlugin = false;

            foreach (dynamic item in Project.Value)
            {
                string key = item.Key;

                if (item.Key.ToString() == "PublishContent")
                {
                    PackagedDir = item.Value;
                }

                if (key.ToString() == "UEPlugin")
                {
                    bIsUEPlugin = true;
                }

                if (key.ToString() == "GitHubRepo")
                {
                    GitHubAuthor = item.Value.Split('/')[0];
                }
            }

            string ProjectVersion = "1.0.0";
            int major = 1;
            int minor = 0;
            int patch = 0;

            try
            {
                var releases = client.Repository.Release.GetAll(GitHubAuthor, ProjectName);
                var latest = releases.Result[0];

                string[] version = latest.Name.Split('.');
                major = Int32.Parse(version[0]);
                minor = Int32.Parse(version[1]);
                patch = Int32.Parse(version[2]);

                ProjectVersion = $"{major}.{minor}.{patch + 1}";
            }
            catch { }

            // Increment the UPlugin file if this is a Unreal Plugin
            if (bIsUEPlugin)
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                string json = File.ReadAllText($"{WorkspaceDir}\\{PackagedDir}\\{ProjectName}.uplugin");
                dynamic uplugin_config = serializer.Deserialize<dynamic>(json);

                uplugin_config["Version"] = patch + 2;
                uplugin_config["VersionName"] = ProjectVersion;

                File.WriteAllText($"{WorkspaceDir}\\{PackagedDir}\\{ProjectName}.uplugin", FormatJson(serializer.Serialize(uplugin_config)).Replace("\\u0027", "'"));
            }

            string ZippedName = $"{ProjectName}-{ProjectVersion}-{BuildConfiguration}.zip";
            string FolderToZip = $"{WorkspaceDir}\\{PackagedDir}";

            if (File.Exists($"{WorkspaceDir}\\{ZippedName}"))
            {
                File.Delete($"{WorkspaceDir}\\{ZippedName}");
            }

            ZipFile.CreateFromDirectory(FolderToZip, $"{WorkspaceDir}\\{ZippedName}");

            // upload zipped folder

            var newRelease = new NewRelease(ProjectVersion);
            newRelease.Name = ProjectVersion;
            newRelease.Draft = false;
            newRelease.Prerelease = false;

            var result = client.Repository.Release.Create(GitHubAuthor, ProjectName, newRelease);

            Print($"Uploading {WorkspaceDir}\\{ZippedName}");
            using (var archiveContents = File.OpenRead($"{WorkspaceDir}\\{ZippedName}"))
            {
                var assetUpload = new ReleaseAssetUpload()
                {
                    FileName = ZippedName,
                    ContentType = "application/zip",
                    RawData = archiveContents
                };
                var release = client.Repository.Release.Get(GitHubAuthor, ProjectName, result.Result.Id);
                var asset = await client.Repository.Release.UploadAsset(release.Result, assetUpload);
            }
        }

        static async Task Main(string[] args)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            string json = File.ReadAllText(GlobalConfig.ConfigFile);

            if (string.IsNullOrEmpty(json))
            {
                PrintError("The config was empty or didn't exist.");
                return;
            }

            dynamic array = serializer.Deserialize<dynamic>(json);

            if (array == null)
            {
                PrintError("Something went wrong with reading the config. Check it out.");
                return;
            }

            if (args.Length < 3)
            {
                PrintError("You don't have enough arguments to run this command. Expecting JenkinsBuilder.exe <project> <command|Build,BuildPublish,Publish> <workspace>");
                return;
            }

            if (!File.Exists(GlobalConfig.GitHubTokenFile))
            {
                PrintError("The GitHubToken.txt file doesn't exist.");
                return;
            }

            GitHubToken = File.ReadAllText(GlobalConfig.GitHubTokenFile);

            string ProjectName = args[0];
            string Command = args[1];
            string Workspace = args[2];

            if (!Directory.Exists(Workspace))
            {
                PrintError("The specified workspace doesn't exist!");
                return;
            }

            dynamic SelectedProject = null;

            // iterate over the config
            foreach (dynamic item in array)
            {
                string project = item.Key.ToString();

                if (project != ProjectName)
                {
                    continue;
                }

                SelectedProject = item;
            }

            if (Command == "Build")
            {
                await Build(SelectedProject, Workspace);
            }

            if (Command == "BuildPublish")
            {
                await Build(SelectedProject, Workspace, true);
            }

            if (Command == "Publish")
            {
                await Publish(SelectedProject, Workspace);
            }
        }
    }
}
