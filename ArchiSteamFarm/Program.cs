﻿/*
    _                _      _  ____   _                           _____
   / \    _ __  ___ | |__  (_)/ ___| | |_  ___   __ _  _ __ ___  |  ___|__ _  _ __  _ __ ___
  / _ \  | '__|/ __|| '_ \ | |\___ \ | __|/ _ \ / _` || '_ ` _ \ | |_  / _` || '__|| '_ ` _ \
 / ___ \ | |  | (__ | | | || | ___) || |_|  __/| (_| || | | | | ||  _|| (_| || |   | | | | | |
/_/   \_\|_|   \___||_| |_||_||____/  \__|\___| \__,_||_| |_| |_||_|   \__,_||_|   |_| |_| |_|

 Copyright 2015-2016 Łukasz "JustArchi" Domeradzki
 Contact: JustArchi@JustArchi.net

 Licensed under the Apache License, Version 2.0 (the "License");
 you may not use this file except in compliance with the License.
 You may obtain a copy of the License at

 http://www.apache.org/licenses/LICENSE-2.0
					
 Unless required by applicable law or agreed to in writing, software
 distributed under the License is distributed on an "AS IS" BASIS,
 WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 See the License for the specific language governing permissions and
 limitations under the License.

*/

using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.JSON;

namespace ArchiSteamFarm {
	internal static class Program {
		internal enum EUserInputType : byte {
			Unknown,
			DeviceID,
			Login,
			Password,
			PhoneNumber,
			SMS,
			SteamGuard,
			SteamParentalPIN,
			RevocationCode,
			TwoFactorAuthentication,
			WCFHostname
		}

		private enum EMode : byte {
			[SuppressMessage("ReSharper", "UnusedMember.Local")]
			Unknown,
			Normal, // Standard most common usage
			Client, // WCF client only
			Server // Normal + WCF server
		}

		internal const string ASF = "ASF";
		internal const string ConfigDirectory = "config";
		internal const string DebugDirectory = "debug";
		internal const string LogFile = "log.txt";

		private const string GithubReleaseURL = "https://api.github.com/repos/" + SharedInfo.GithubRepo + "/releases"; // GitHub API is HTTPS only
		private const string GlobalConfigFileName = ASF + ".json";
		private const string GlobalDatabaseFileName = ASF + ".db";

		internal static readonly Version Version = Assembly.GetEntryAssembly().GetName().Version;

		private static readonly object ConsoleLock = new object();
		private static readonly ManualResetEventSlim ShutdownResetEvent = new ManualResetEventSlim(false);
		private static readonly WCF WCF = new WCF();

		internal static bool IsRunningAsService { get; private set; }
		internal static GlobalConfig GlobalConfig { get; private set; }
		internal static GlobalDatabase GlobalDatabase { get; private set; }

		private static bool ShutdownSequenceInitialized;
		private static Timer AutoUpdatesTimer;
		private static EMode Mode = EMode.Normal;
		private static WebBrowser WebBrowser;

		internal static async Task CheckForUpdate(bool updateOverride = false) {
			string exeFile = Assembly.GetEntryAssembly().Location;
			string oldExeFile = exeFile + ".old";

			// We booted successfully so we can now remove old exe file
			if (File.Exists(oldExeFile)) {
				// It's entirely possible that old process is still running, allow at least a second before trying to remove the file
				await Task.Delay(1000).ConfigureAwait(false);

				try {
					File.Delete(oldExeFile);
				} catch (Exception e) {
					Logging.LogGenericException(e);
					Logging.LogGenericError("Could not remove old ASF binary, please remove " + oldExeFile + " manually in order for update function to work!");
				}
			}

			if (GlobalConfig.UpdateChannel == GlobalConfig.EUpdateChannel.Unknown) {
				return;
			}

			if ((AutoUpdatesTimer == null) && GlobalConfig.AutoUpdates) {
				AutoUpdatesTimer = new Timer(
					async e => await CheckForUpdate().ConfigureAwait(false),
					null,
					TimeSpan.FromDays(1), // Delay
					TimeSpan.FromDays(1) // Period
				);

				Logging.LogGenericInfo("ASF will automatically check for new versions every 24 hours");
			}

			string releaseURL = GithubReleaseURL;
			if (GlobalConfig.UpdateChannel == GlobalConfig.EUpdateChannel.Stable) {
				releaseURL += "/latest";
			}

			Logging.LogGenericInfo("Checking new version...");

			string response = await WebBrowser.UrlGetToContentRetry(releaseURL).ConfigureAwait(false);
			if (string.IsNullOrEmpty(response)) {
				Logging.LogGenericWarning("Could not check latest version!");
				return;
			}

			GitHub.ReleaseResponse releaseResponse;
			if (GlobalConfig.UpdateChannel == GlobalConfig.EUpdateChannel.Stable) {
				try {
					releaseResponse = JsonConvert.DeserializeObject<GitHub.ReleaseResponse>(response);
				} catch (JsonException e) {
					Logging.LogGenericException(e);
					return;
				}
			} else {
				List<GitHub.ReleaseResponse> releases;
				try {
					releases = JsonConvert.DeserializeObject<List<GitHub.ReleaseResponse>>(response);
				} catch (JsonException e) {
					Logging.LogGenericException(e);
					return;
				}

				if ((releases == null) || (releases.Count == 0)) {
					Logging.LogGenericWarning("Could not check latest version!");
					return;
				}

				releaseResponse = releases[0];
			}

			if (string.IsNullOrEmpty(releaseResponse.Tag)) {
				Logging.LogGenericWarning("Could not check latest version!");
				return;
			}

			Version newVersion = new Version(releaseResponse.Tag);

			Logging.LogGenericInfo("Local version: " + Version + " | Remote version: " + newVersion);

			if (Version.CompareTo(newVersion) >= 0) { // If local version is the same or newer than remote version
				return;
			}

			if (!updateOverride && !GlobalConfig.AutoUpdates) {
				Logging.LogGenericInfo("New version is available!");
				Logging.LogGenericInfo("Consider updating yourself!");
				await Task.Delay(5000).ConfigureAwait(false);
				return;
			}

			if (File.Exists(oldExeFile)) {
				Logging.LogGenericWarning("Refusing to proceed with auto update as old " + oldExeFile + " binary could not be removed, please remove it manually");
				return;
			}

			// Auto update logic starts here
			if (releaseResponse.Assets == null) {
				Logging.LogGenericWarning("Could not proceed with update because that version doesn't include assets!");
				return;
			}

			string exeFileName = Path.GetFileName(exeFile);
			GitHub.ReleaseResponse.Asset binaryAsset = releaseResponse.Assets.FirstOrDefault(asset => !string.IsNullOrEmpty(asset.Name) && asset.Name.Equals(exeFileName, StringComparison.OrdinalIgnoreCase));

			if (binaryAsset == null) {
				Logging.LogGenericWarning("Could not proceed with update because there is no asset that relates to currently running binary!");
				return;
			}

			if (string.IsNullOrEmpty(binaryAsset.DownloadURL)) {
				Logging.LogGenericWarning("Could not proceed with update because download URL is empty!");
				return;
			}

			Logging.LogGenericInfo("Downloading new version...");
			Logging.LogGenericInfo("While waiting, consider donating if you appreciate the work being done :)");

			byte[] result = await WebBrowser.UrlGetToBytesRetry(binaryAsset.DownloadURL).ConfigureAwait(false);
			if (result == null) {
				return;
			}

			string newExeFile = exeFile + ".new";

			// Firstly we create new exec
			try {
				File.WriteAllBytes(newExeFile, result);
			} catch (Exception e) {
				Logging.LogGenericException(e);
				return;
			}

			// Now we move current -> old
			try {
				File.Move(exeFile, oldExeFile);
			} catch (Exception e) {
				Logging.LogGenericException(e);
				try {
					// Cleanup
					File.Delete(newExeFile);
				} catch {
					// Ignored
				}
				return;
			}

			// Now we move new -> current
			try {
				File.Move(newExeFile, exeFile);
			} catch (Exception e) {
				Logging.LogGenericException(e);
				try {
					// Cleanup
					File.Move(oldExeFile, exeFile);
					File.Delete(newExeFile);
				} catch {
					// Ignored
				}
				return;
			}

			Logging.LogGenericInfo("Update process finished!");

			if (GlobalConfig.AutoRestart) {
				Logging.LogGenericInfo("Restarting...");
				await Task.Delay(5000).ConfigureAwait(false);
				Restart();
			} else {
				Logging.LogGenericInfo("Exiting...");
				await Task.Delay(5000).ConfigureAwait(false);
				Exit();
			}
		}

		internal static void Exit(byte exitCode = 0) {
			Shutdown();
			Environment.Exit(exitCode);
		}

		internal static void Restart() {
			InitShutdownSequence();

			try {
				Process.Start(Assembly.GetEntryAssembly().Location, string.Join(" ", Environment.GetCommandLineArgs().Skip(1)));
			} catch (Exception e) {
				Logging.LogGenericException(e);
			}

			Environment.Exit(0);
		}

		internal static string GetUserInput(EUserInputType userInputType, string botName = ASF, string extraInformation = null) {
			if (userInputType == EUserInputType.Unknown) {
				return null;
			}

			if (GlobalConfig.Headless || !Runtime.IsUserInteractive) {
				Logging.LogGenericWarning("Received a request for user input, but process is running in headless mode!");
				return null;
			}

			string result;
			lock (ConsoleLock) {
				Logging.OnUserInputStart();
				switch (userInputType) {
					case EUserInputType.DeviceID:
						Console.Write("<" + botName + "> Please enter your Device ID (including \"android:\"): ");
						break;
					case EUserInputType.Login:
						Console.Write("<" + botName + "> Please enter your login: ");
						break;
					case EUserInputType.Password:
						Console.Write("<" + botName + "> Please enter your password: ");
						break;
					case EUserInputType.PhoneNumber:
						Console.Write("<" + botName + "> Please enter your full phone number (e.g. +1234567890): ");
						break;
					case EUserInputType.SMS:
						Console.Write("<" + botName + "> Please enter SMS code sent on your mobile: ");
						break;
					case EUserInputType.SteamGuard:
						Console.Write("<" + botName + "> Please enter the auth code sent to your email: ");
						break;
					case EUserInputType.SteamParentalPIN:
						Console.Write("<" + botName + "> Please enter steam parental PIN: ");
						break;
					case EUserInputType.RevocationCode:
						Console.WriteLine("<" + botName + "> PLEASE WRITE DOWN YOUR REVOCATION CODE: " + extraInformation);
						Console.Write("<" + botName + "> Hit enter once ready...");
						break;
					case EUserInputType.TwoFactorAuthentication:
						Console.Write("<" + botName + "> Please enter your 2 factor auth code from your authenticator app: ");
						break;
					case EUserInputType.WCFHostname:
						Console.Write("<" + botName + "> Please enter your WCF hostname: ");
						break;
					default:
						Console.Write("<" + botName + "> Please enter not documented yet value of \"" + userInputType + "\": ");
						break;
				}

				result = Console.ReadLine();

				if (!Console.IsOutputRedirected) {
					Console.Clear(); // For security purposes
				}

				Logging.OnUserInputEnd();
			}

			return !string.IsNullOrEmpty(result) ? result.Trim() : null;
		}

		internal static void OnBotShutdown() {
			if (ShutdownSequenceInitialized) {
				return;
			}

			if (Bot.Bots.Values.Any(bot => bot.KeepRunning)) {
				return;
			}

			if (WCF.IsServerRunning()) {
				return;
			}

			Logging.LogGenericInfo("No bots are running, exiting");
			Thread.Sleep(5000);
			ShutdownResetEvent.Set();
		}

		private static void Shutdown() {
			if (!InitShutdownSequence()) {
				return;
			}

			ShutdownResetEvent.Set();
		}

		private static bool InitShutdownSequence() {
			if (ShutdownSequenceInitialized) {
				return false;
			}

			ShutdownSequenceInitialized = true;

			WCF.StopServer();
			foreach (Bot bot in Bot.Bots.Values) {
				bot.Stop();
			}

			return true;
		}

		private static void InitServices() {
			string globalConfigFile = Path.Combine(ConfigDirectory, GlobalConfigFileName);

			GlobalConfig = GlobalConfig.Load(globalConfigFile);
			if (GlobalConfig == null) {
				Logging.LogGenericError("Global config could not be loaded, please make sure that " + globalConfigFile + " exists and is valid!");
				Thread.Sleep(5000);
				Exit(1);
			}

			string globalDatabaseFile = Path.Combine(ConfigDirectory, GlobalDatabaseFileName);

			GlobalDatabase = GlobalDatabase.Load(globalDatabaseFile);
			if (GlobalDatabase == null) {
				Logging.LogGenericError("Global database could not be loaded, if issue persists, please remove " + globalDatabaseFile + " in order to recreate database!");
				Thread.Sleep(5000);
				Exit(1);
			}

			ArchiWebHandler.Init();
			WebBrowser.Init();
			WCF.Init();

			WebBrowser = new WebBrowser(ASF);
		}

		private static void ParsePreInitArgs(IEnumerable<string> args) {
			if (args == null) {
				Logging.LogNullError(nameof(args));
				return;
			}

			foreach (string arg in args) {
				switch (arg) {
					case "":
						break;
					default:
						if (arg.StartsWith("--", StringComparison.Ordinal)) {
							if (arg.StartsWith("--path=", StringComparison.Ordinal) && (arg.Length > 7)) {
								Directory.SetCurrentDirectory(arg.Substring(7));
							}
						}

						break;
				}
			}
		}

		private static void ParsePostInitArgs(IEnumerable<string> args) {
			if (args == null) {
				Logging.LogNullError(nameof(args));
				return;
			}

			foreach (string arg in args) {
				switch (arg) {
					case "":
						break;
					case "--client":
						Mode = EMode.Client;
						break;
					case "--server":
						Mode = EMode.Server;
						WCF.StartServer();
						break;
					default:
						if (arg.StartsWith("--", StringComparison.Ordinal)) {
							if (arg.StartsWith("--cryptkey=", StringComparison.Ordinal) && (arg.Length > 11)) {
								CryptoHelper.SetEncryptionKey(arg.Substring(11));
							}

							break;
						}

						if (Mode != EMode.Client) {
							Logging.LogGenericWarning("Ignoring command because --client wasn't specified: " + arg);
							break;
						}

						Logging.LogGenericInfo("Command sent: " + arg);
						Logging.LogGenericInfo("Response received: " + WCF.SendCommand(arg));
						break;
				}
			}
		}

		private static void UnhandledExceptionHandler(object sender, UnhandledExceptionEventArgs args) {
			if (args?.ExceptionObject == null) {
				Logging.LogNullError(nameof(args) + " || " + nameof(args.ExceptionObject));
				return;
			}

			Logging.LogFatalException((Exception) args.ExceptionObject);
		}

		private static void UnobservedTaskExceptionHandler(object sender, UnobservedTaskExceptionEventArgs args) {
			if (args?.Exception == null) {
				Logging.LogNullError(nameof(args) + " || " + nameof(args.Exception));
				return;
			}

			Logging.LogFatalException(args.Exception);
		}

		private static void Init(string[] args) {
			AppDomain.CurrentDomain.UnhandledException += UnhandledExceptionHandler;
			TaskScheduler.UnobservedTaskException += UnobservedTaskExceptionHandler;

			Logging.InitCoreLoggers();
			Logging.LogGenericInfo("ASF V" + Version);

			if (!Runtime.IsRuntimeSupported()) {
				Logging.LogGenericError("ASF detected unsupported runtime version, program might NOT run correctly in current environment. You're running it at your own risk!");
				Thread.Sleep(10000);
			}

			string homeDirectory = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
			if (!string.IsNullOrEmpty(homeDirectory)) {
				Directory.SetCurrentDirectory(homeDirectory);

				// Allow loading configs from source tree if it's a debug build
				if (Debugging.IsDebugBuild) {

					// Common structure is bin/(x64/)Debug/ArchiSteamFarm.exe, so we allow up to 4 directories up
					for (byte i = 0; i < 4; i++) {
						Directory.SetCurrentDirectory("..");
						if (Directory.Exists(ConfigDirectory)) {
							break;
						}
					}

					// If config directory doesn't exist after our adjustment, abort all of that
					if (!Directory.Exists(ConfigDirectory)) {
						Directory.SetCurrentDirectory(homeDirectory);
					}
				}
			}

			// Parse pre-init args
			if (args != null) {
				ParsePreInitArgs(args);
			}

			InitServices();

			// If debugging is on, we prepare debug directory prior to running
			if (GlobalConfig.Debug) {
				if (Directory.Exists(DebugDirectory)) {
					Directory.Delete(DebugDirectory, true);
					Thread.Sleep(1000); // Dirty workaround giving Windows some time to sync
				}
				Directory.CreateDirectory(DebugDirectory);

				SteamKit2.DebugLog.AddListener(new Debugging.DebugListener(Path.Combine(DebugDirectory, "debug.txt")));
				SteamKit2.DebugLog.Enabled = true;
			}

			// Parse post-init args
			if (args != null) {
				ParsePostInitArgs(args);
			}

			// If we ran ASF as a client, we're done by now
			if (Mode == EMode.Client) {
				Exit();
			}

			// From now on it's server mode
			Logging.InitEnhancedLoggers();

			if (!Directory.Exists(ConfigDirectory)) {
				Logging.LogGenericError("Config directory doesn't exist!");
				Thread.Sleep(5000);
				Exit(1);
			}

			CheckForUpdate().Wait();

			// Before attempting to connect, initialize our list of CMs
			Bot.InitializeCMs(GlobalDatabase.CellID, GlobalDatabase.ServerListProvider);

			bool isRunning = false;

			foreach (string botName in Directory.EnumerateFiles(ConfigDirectory, "*.json").Select(Path.GetFileNameWithoutExtension)) {
				switch (botName) {
					case ASF:
					case "example":
					case "minimal":
						continue;
				}

				Bot bot = new Bot(botName);
				if ((bot.BotConfig == null) || !bot.BotConfig.Enabled) {
					continue;
				}

				if (bot.BotConfig.StartOnLaunch) {
					isRunning = true;
				}
			}

			// Check if we got any bots running
			if (!isRunning) {
				OnBotShutdown();
			}
		}

		private static void Main(string[] args) {
			if (Runtime.IsUserInteractive) {
				// App
				Init(args);

				// Wait for signal to shutdown
				ShutdownResetEvent.Wait();

				// We got a signal to shutdown
				Exit();
			} else {
				// Service
				IsRunningAsService = true;
				using (Service service = new Service()) {
					ServiceBase.Run(service);
				}
			}
		}

		private sealed class Service : ServiceBase {
			internal Service() {
				ServiceName = SharedInfo.ServiceName;
			}

			protected override void OnStart(string[] args) => Task.Run(() => {
				Init(args);
				ShutdownResetEvent.Wait();
				Stop();
			});

			protected override void OnStop() => Shutdown();
		}
	}

}
