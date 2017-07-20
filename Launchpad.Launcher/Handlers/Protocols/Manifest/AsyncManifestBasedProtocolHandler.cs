//
//  ManifestBasedProtocolHandler.cs
//
//  Author:
//       Jarl Gullberg <jarl.gullberg@gmail.com>
//
//  Copyright (c) 2016 Jarl Gullberg
//
//  This program is free software: you can redistribute it and/or modify
//  it under the terms of the GNU General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  This program is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU General Public License for more details.
//
//  You should have received a copy of the GNU General Public License
//  along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Policy;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using Launchpad.Common;
using Launchpad.Common.Enums;
using Launchpad.Common.Handlers;
using Launchpad.Common.Handlers.Manifest;
using Launchpad.Launcher.Utility;
using NGettext;

namespace Launchpad.Launcher.Handlers.Protocols.Manifest
{
	/// <summary>
	/// Base underlying class for protocols using a manifest.
	/// </summary>
	public abstract class AsyncManifestBasedProtocolHandler : AsyncPatchProtocolHandler
	{
		/// <summary>
		/// The localization catalog.
		/// </summary>
		private static readonly ICatalog LocalizationCatalog = new Catalog("Launchpad", "./Content/locale");

		/// <summary>
		/// Logger instance for this class.
		/// </summary>
		private static readonly ILog Log = LogManager.GetLogger(typeof(ManifestBasedProtocolHandler));

		/// <summary>
		/// The file manifest handler. This allows access to the launcher and game file lists.
		/// </summary>
		private readonly ManifestHandler FileManifestHandler;

		/// <summary>
		/// Creates a new instance of the <see cref="ManifestBasedProtocolHandler"/> class.
		/// </summary>
		protected AsyncManifestBasedProtocolHandler()
		{
			this.FileManifestHandler = new ManifestHandler
			(
				ConfigHandler.GetLocalDir(),
				new Url(this.Config.GetBaseProtocolURL()),
				this.Config.GetSystemTarget()
			);
		}

		/// <summary>
		/// Installs the game.
		/// </summary>
		public override async Task InstallGameAsync(CancellationToken ct)
		{
			try
			{
				// Create the .install file to mark that an installation has begun.
				// If it exists, do nothing.
				ConfigHandler.CreateGameCookie();

				// Make sure the manifest is up to date
				await RefreshModuleManifestAsync(ct, EModule.Game);

				// Download Game
				await DownloadModuleAsync(ct, EModule.Game);

				// Verify Game
				await VerifyModuleAsync(ct, EModule.Game);
			}
			catch (IOException ioex)
			{
				Log.Warn("Game installation failed (IOException): " + ioex.Message);
			}

			// OnModuleInstallationFinished and OnModuleInstallationFailed is in VerifyGame
			// in order to allow it to run as a standalone action, while still keeping this functional.

			// As a side effect, it is required that it is the last action to run in Install and Update,
			// which happens to coincide with the general design.
		}

		/// <summary>
		/// Updates the specified module to the latest version.
		/// </summary>
		/// <param name="module">The module to update.</param>
		/// <exception cref="ArgumentOutOfRangeException">
		/// Will be thrown if the <see cref="EModule"/> passed to the function is not a valid value.
		/// </exception>
		public override async Task UpdateModuleAsync(CancellationToken ct, EModule module)
		{
			List<ManifestEntry> manifest;
			List<ManifestEntry> oldManifest;
			switch (module)
			{
				case EModule.Launcher:
				{
					await RefreshModuleManifestAsync(ct, EModule.Launcher);

					this.ModuleInstallFinishedArgs.Module = EModule.Launcher;
					this.ModuleInstallFailedArgs.Module = EModule.Launcher;
					manifest = this.FileManifestHandler.GetManifest(EManifestType.Launchpad, false);
					oldManifest = this.FileManifestHandler.GetManifest(EManifestType.Launchpad, true);
					break;
				}
				case EModule.Game:
				{
					await RefreshModuleManifestAsync(ct, EModule.Game);

					this.ModuleInstallFinishedArgs.Module = EModule.Game;
					this.ModuleInstallFailedArgs.Module = EModule.Game;
					manifest = this.FileManifestHandler.GetManifest(EManifestType.Game, false);
					oldManifest = this.FileManifestHandler.GetManifest(EManifestType.Game, true);
					break;
				}
				default:
				{
					throw new ArgumentOutOfRangeException(nameof(module), module, "An invalid module value was passed to UpdateModule.");
				}
			}

			// Check to see if we have valid manifests
			if (manifest == null)
			{
				Log.Error($"No manifest was found when updating the module \"{module}\". The server files may be inaccessible or missing.");
				OnModuleInstallationFailed();
				return;
			}

			// This dictionary holds a list of new entries and their equivalents from the old manifest. It is used
			// to determine whether or not a file is partial, or merely old yet smaller.
			Dictionary<ManifestEntry, ManifestEntry> oldEntriesBeingReplaced = new Dictionary<ManifestEntry, ManifestEntry>();
			List<ManifestEntry> filesRequiringUpdate = new List<ManifestEntry>();
			foreach (ManifestEntry fileEntry in manifest)
			{
				filesRequiringUpdate.Add(fileEntry);
				if (oldManifest != null)
				{
					if (!oldManifest.Contains(fileEntry))
					{
						// See if there is an old entry which matches the new one.
						ManifestEntry matchingOldEntry =
							oldManifest.FirstOrDefault(oldEntry => oldEntry.RelativePath == fileEntry.RelativePath);

						if (matchingOldEntry != null)
						{
							oldEntriesBeingReplaced.Add(fileEntry, matchingOldEntry);
						}
					}
				}
			}

			try
			{
				int updatedFiles = 0;
				foreach (ManifestEntry fileEntry in filesRequiringUpdate)
				{
					++updatedFiles;

					this.ModuleUpdateProgressArgs.IndicatorLabelMessage = GetUpdateIndicatorLabelMessage(Path.GetFileName(fileEntry.RelativePath),
						updatedFiles,
						filesRequiringUpdate.Count);
					OnModuleUpdateProgressChanged();

					// If we're updating an existing file, make sure to let the downloader know
					if (oldEntriesBeingReplaced.ContainsKey(fileEntry))
					{
						await DownloadManifestEntryAsync(ct, fileEntry, module, oldEntriesBeingReplaced[fileEntry]);
					}
					else
					{
						await DownloadManifestEntryAsync(ct, fileEntry, module);
					}
				}
			}
			catch (IOException ioex)
			{
				Log.Warn($"Updating of {module} files failed (IOException): " + ioex.Message);
				OnModuleInstallationFailed();
				return;
			}

			OnModuleInstallationFinished();
		}

		/// <summary>
		/// Verifies and repairs the files of the specified module.
		/// </summary>
		public override async Task VerifyModuleAsync(CancellationToken ct, EModule module)
		{
			List<ManifestEntry> manifest = this.FileManifestHandler.GetManifest((EManifestType) module, false);
			List<ManifestEntry> brokenFiles = new List<ManifestEntry>();

			if (manifest == null)
			{
				Log.Error($"No manifest was found when verifying the module \"{module}\". The server files may be inaccessible or missing.");
				OnModuleInstallationFailed();
				return;
			}

			try
			{
				int verifiedFiles = 0;
				foreach (ManifestEntry fileEntry in manifest)
				{
					++verifiedFiles;

					// Prepare the progress event contents
					this.ModuleVerifyProgressArgs.IndicatorLabelMessage = GetVerifyIndicatorLabelMessage(Path.GetFileName(fileEntry.RelativePath),
						verifiedFiles, manifest.Count);
					OnModuleVerifyProgressChanged();

					if (!fileEntry.IsFileIntegrityIntact())
					{
						brokenFiles.Add(fileEntry);
						Log.Info($"File \"{Path.GetFileName(fileEntry.RelativePath)}\" failed its integrity check and was queued for redownload.");
					}
				}

				int downloadedFiles = 0;
				foreach (ManifestEntry fileEntry in brokenFiles)
				{
					++downloadedFiles;

					// Prepare the progress event contents
					this.ModuleDownloadProgressArgs.IndicatorLabelMessage = GetDownloadIndicatorLabelMessage(Path.GetFileName(fileEntry.RelativePath),
						downloadedFiles, brokenFiles.Count);
					OnModuleDownloadProgressChanged();

					for (int i = 0; i < this.Config.GetFileRetries(); ++i)
					{
						if (!fileEntry.IsFileIntegrityIntact())
						{
							await DownloadManifestEntryAsync(ct, fileEntry, module);
							Log.Info($"File \"{Path.GetFileName(fileEntry.RelativePath)}\" failed its integrity check again after redownloading. ({i} retries)");
						}
						else
						{
							break;
						}
					}
				}
			}
			catch (IOException ioex)
			{
				Log.Warn($"Verification of {module} files failed (IOException): " + ioex.Message);
				OnModuleInstallationFailed();
			}

			OnModuleInstallationFinished();
		}

		/// <summary>
		/// Downloads the latest version of the specified module.
		/// </summary>
		/// <exception cref="ArgumentOutOfRangeException">
		/// Will be thrown if the <see cref="EModule"/> passed to the function is not a valid value.
		/// </exception>
		protected override async Task DownloadModuleAsync(CancellationToken ct, EModule module)
		{
			List<ManifestEntry> moduleManifest;
			switch (module)
			{
				case EModule.Launcher:
				{
					await RefreshModuleManifestAsync(ct, EModule.Launcher);

					this.ModuleInstallFinishedArgs.Module = EModule.Launcher;
					this.ModuleInstallFailedArgs.Module = EModule.Launcher;
					moduleManifest = this.FileManifestHandler.GetManifest(EManifestType.Launchpad, false);
					break;
				}
				case EModule.Game:
				{
					await RefreshModuleManifestAsync(ct, EModule.Game);

					this.ModuleInstallFinishedArgs.Module = EModule.Game;
					this.ModuleInstallFailedArgs.Module = EModule.Game;
					moduleManifest = this.FileManifestHandler.GetManifest(EManifestType.Game, false);
					break;
				}
				default:
				{
					throw new ArgumentOutOfRangeException(nameof(module), module,
						"An invalid module value was passed to DownloadModule.");
				}
			}

			if (moduleManifest == null)
			{
				Log.Error($"No manifest was found when installing the module \"{module}\". The server files may be inaccessible or missing.");
				OnModuleInstallationFailed();
				return;
			}

			// In order to be able to resume downloading, we check if there is an entry
			// stored in the install cookie.

			// Attempt to parse whatever is inside the install cookie
			ManifestEntry lastDownloadedFile;
			if (ManifestEntry.TryParse(File.ReadAllText(ConfigHandler.GetGameCookiePath()), out lastDownloadedFile))
			{
				// Loop through all the entries in the manifest until we encounter
				// an entry which matches the one in the install cookie

				foreach (ManifestEntry fileEntry in moduleManifest)
				{
					if (lastDownloadedFile == fileEntry)
					{
						// Remove all entries before the one we were last at.
						moduleManifest.RemoveRange(0, moduleManifest.IndexOf(fileEntry));
					}
				}
			}

			int downloadedFiles = 0;
			foreach (ManifestEntry fileEntry in moduleManifest)
			{
				++downloadedFiles;

				// Prepare the progress event contents
				this.ModuleDownloadProgressArgs.IndicatorLabelMessage = GetDownloadIndicatorLabelMessage(Path.GetFileName(fileEntry.RelativePath),
					downloadedFiles, moduleManifest.Count);
				OnModuleDownloadProgressChanged();

				await DownloadManifestEntryAsync(ct, fileEntry, module);
			}
		}

		/// <summary>
		/// Reads the contents of a remote file as a string.
		/// </summary>
		protected abstract Task<string> ReadRemoteFileAsync(CancellationToken ct, string url, bool useAnonymousLong = false);

		/// <summary>
		/// Downloads the contents of the file at the specified url to the specified local path.
		/// This method supported resuming a partial file.
		/// </summary>
		protected abstract Task DownloadRemoteFileAsync(CancellationToken ct, string url, string localPath, long totalSize = 0, long contentOffset = 0,
			bool useAnonymousLogin = false);

		/// <summary>
		/// Determines whether or not the specified module is outdated.
		/// </summary>
		/// <exception cref="ArgumentOutOfRangeException">
		/// Will be thrown if the <see cref="EModule"/> passed to the function is not a valid value.
		/// </exception>
		public override async Task<bool> IsModuleOutdatedAsync(CancellationToken ct, EModule module)
		{
			try
			{
				Version local;
				Version remote;

				switch (module)
				{
					case EModule.Launcher:
					{
						local = this.Config.GetLocalLauncherVersion();
						remote = await GetRemoteLauncherVersionAsync(ct);
						break;
					}
					case EModule.Game:
					{
						local = this.Config.GetLocalGameVersion();
						remote = await GetRemoteGameVersionAsync(ct);
						break;
					}
					default:
					{
						throw new ArgumentOutOfRangeException(nameof(module), module,
							"An invalid module value was passed to IsModuleOutdated.");
					}
				}

				return local < remote;
			}
			catch (WebException wex)
			{
				Log.Warn("Unable to determine whether or not the launcher was outdated (WebException): " + wex.Message);
				return false;
			}
		}

		/// <summary>
		/// Downloads the file referred to by the specifed manifest entry.
		/// </summary>
		/// <param name="fileEntry">The entry to download.</param>
		/// <param name="module">The module that the entry belongs to.</param>
		/// <param name="oldFileEntry">The old entry, if one exists.</param>
		/// <exception cref="ArgumentOutOfRangeException">
		/// Will be thrown if the <see cref="EModule"/> passed to the function is not a valid value.
		/// </exception>
		/// <exception cref="ArgumentNullException">
		/// Will be thrown if the local path set in the <paramref name="fileEntry"/> passed to the function is not a valid value.
		/// </exception>
		protected virtual async Task DownloadManifestEntryAsync(CancellationToken ct, ManifestEntry fileEntry, EModule module, ManifestEntry oldFileEntry = null)
		{
			this.ModuleDownloadProgressArgs.Module = module;

			string baseRemoteURL;
			string baseLocalPath;
			switch (module)
			{
				case EModule.Launcher:
				{
					baseRemoteURL = this.Config.GetLauncherBinariesURL();
					baseLocalPath = ConfigHandler.GetTempLauncherDownloadPath();
					break;
				}
				case EModule.Game:
				{
					baseRemoteURL = this.Config.GetGameURL();
					baseLocalPath = this.Config.GetGamePath();
					break;
				}
				default:
				{
					throw new ArgumentOutOfRangeException(nameof(module), module,
						"An invalid module value was passed to DownloadManifestEntry.");
				}
			}

			// Build the access strings
			string remoteURL = $"{baseRemoteURL}{fileEntry.RelativePath}";
			string localPath = $"{baseLocalPath}{fileEntry.RelativePath}";

			// Make sure we have a directory to put the file in
			if (!string.IsNullOrEmpty(localPath))
			{
				string localPathParentDir = Path.GetDirectoryName(localPath);
				if (!Directory.Exists(localPathParentDir))
				{
					Directory.CreateDirectory(Path.GetDirectoryName(localPath));
				}
			}
			else
			{
				throw new ArgumentNullException(nameof(localPath), "The local path was null or empty.");
			}

			// Reset the cookie
			File.WriteAllText(ConfigHandler.GetGameCookiePath(), string.Empty);

			// Write the current file progress to the install cookie
			using (TextWriter textWriterProgress = new StreamWriter(ConfigHandler.GetGameCookiePath()))
			{
				textWriterProgress.WriteLine(fileEntry);
				textWriterProgress.Flush();
			}

			// First, let's see if an old file exists, and is valid.
			if (oldFileEntry != null)
			{
				// Check if the file is present, the correct size, and the correct hash
				if (oldFileEntry.IsFileIntegrityIntact())
				{
					// If it is, delete it.
					File.Delete(localPath);
				}
			}

			if (File.Exists(localPath))
			{
				FileInfo fileInfo = new FileInfo(localPath);
				if (fileInfo.Length != fileEntry.Size)
				{
					// If the file is partial, resume the download.
					if (fileInfo.Length < fileEntry.Size)
					{
						Log.Info($"Resuming interrupted file \"{Path.GetFileNameWithoutExtension(fileEntry.RelativePath)}\" at byte {fileInfo.Length}.");
						await DownloadRemoteFileAsync(ct, remoteURL, localPath, fileEntry.Size, fileInfo.Length);
					}
					else
					{
						// If it's larger than expected, toss it in the bin and try again.
						Log.Info($"Restarting interrupted file \"{Path.GetFileNameWithoutExtension(fileEntry.RelativePath)}\": File bigger than expected.");

						File.Delete(localPath);
						await DownloadRemoteFileAsync(ct, remoteURL, localPath, fileEntry.Size);
					}
				}
				else
				{
					string localHash;
					using (FileStream fs = File.OpenRead(localPath))
					{
						localHash = MD5Handler.GetStreamHash(fs);
					}

					if (localHash != fileEntry.Hash)
					{
						// If the hash doesn't match, toss it in the bin and try again.
						Log.Info($"Redownloading file \"{Path.GetFileNameWithoutExtension(fileEntry.RelativePath)}\": " +
						         $"Hash sum mismatch. Local: {localHash}, Expected: {fileEntry.Hash}");

						File.Delete(localPath);
						await DownloadRemoteFileAsync(ct, remoteURL, localPath, fileEntry.Size);
					}
				}
			}
			else
			{
				// No file, download it
				await DownloadRemoteFileAsync(ct, remoteURL, localPath,fileEntry.Size);
			}

			// We've finished the download, so empty the cookie
			File.WriteAllText(ConfigHandler.GetGameCookiePath(), string.Empty);
		}

		/// <summary>
		/// Determines whether or not the local copy of the manifest for the specifed module is outdated.
		/// </summary>
		/// <exception cref="ArgumentOutOfRangeException">
		/// Will be thrown if the <see cref="EModule"/> passed to the function is not a valid value.
		/// </exception>
		protected virtual async Task<bool> IsModuleManifestOutdatedAsync(CancellationToken ct, EModule module)
		{
			string manifestPath;
			switch (module)
			{
				case EModule.Launcher:
				case EModule.Game:
				{
					manifestPath = this.FileManifestHandler.GetManifestPath((EManifestType) module, false);
					break;
				}
				default:
				{
					throw new ArgumentOutOfRangeException(nameof(module), module,
						"An invalid module value was passed to RefreshModuleManifest.");
				}
			}

			if (File.Exists(manifestPath))
			{
				string remoteHash = await GetRemoteModuleManifestChecksumAsync(ct, module);
				using (Stream file = File.OpenRead(manifestPath))
				{
					string localHash = MD5Handler.GetStreamHash(file);

					return remoteHash != localHash;
				}
			}

			return true;
		}

		/// <summary>
		/// Gets the checksum of the manifest for the specified module.
		/// </summary>
		/// <exception cref="ArgumentOutOfRangeException">
		/// Will be thrown if the <see cref="EModule"/> passed to the function is not a valid value.
		/// </exception>
		protected virtual async Task<string> GetRemoteModuleManifestChecksumAsync(CancellationToken ct, EModule module)
		{
			string checksum;
			switch (module)
			{
				case EModule.Launcher:
				case EModule.Game:
				{
					checksum = await ReadRemoteFileAsync(ct, this.FileManifestHandler.GetManifestChecksumURL((EManifestType)module));
					break;
				}
				default:
				{
					throw new ArgumentOutOfRangeException(nameof(module), module,
						"An invalid module value was passed to GetRemoteModuleManifestChecksum.");
				}
			}

			return checksum.RemoveLineSeparatorsAndNulls();
		}

		/// <summary>
		/// Refreshes the current manifest by redownloading it, if required;
		/// </summary>
		/// <exception cref="ArgumentOutOfRangeException">
		/// Will be thrown if the <see cref="EModule"/> passed to the function is not a valid value.
		/// </exception>
		protected virtual async Task RefreshModuleManifestAsync(CancellationToken ct, EModule module)
		{
			bool manifestExists;
			switch (module)
			{
				case EModule.Launcher:
				case EModule.Game:
				{
					manifestExists = File.Exists(this.FileManifestHandler.GetManifestPath((EManifestType)module, false));
					break;
				}
				default:
				{
					throw new ArgumentOutOfRangeException(nameof(module), module,
						"An invalid module value was passed to RefreshModuleManifest");
				}
			}

			if (manifestExists)
			{
				if (await IsModuleManifestOutdatedAsync(ct, module))
				{
					await DownloadModuleManifestAsync(ct, module);
				}
			}
			else
			{
				await DownloadModuleManifestAsync(ct, module);
			}

			// Now update the handler instance
			this.FileManifestHandler.ReloadManifests((EManifestType)module);
		}

		/// <summary>
		/// Downloads the manifest for the specified module, and backs up the old copy of the manifest.
		/// </summary>
		/// <exception cref="ArgumentOutOfRangeException">
		/// Will be thrown if the <see cref="EModule"/> passed to the function is not a valid value.
		/// </exception>
		protected virtual async Task DownloadModuleManifestAsync(CancellationToken ct, EModule module)
		{
			string remoteURL;
			string localPath;
			string oldLocalPath;
			switch (module)
			{
				case EModule.Launcher:
				case EModule.Game:
				{
					remoteURL = this.FileManifestHandler.GetManifestURL((EManifestType)module);
					localPath = this.FileManifestHandler.GetManifestPath((EManifestType) module, false);
					oldLocalPath = this.FileManifestHandler.GetManifestPath((EManifestType) module, true);

					break;
				}
				default:
				{
					throw new ArgumentOutOfRangeException(nameof(module), module,
						"An invalid module value was passed to DownloadModuleManifest");
				}
			}

			try
			{
				// Delete the old backup (if there is one)
				if (File.Exists(oldLocalPath))
				{
					File.Delete(oldLocalPath);
				}

				// Create a backup of the old manifest so that we can compare them when updating the game
				File.Move(localPath, oldLocalPath);
			}
			catch (IOException ioex)
			{
				Log.Warn("Failed to back up the old launcher manifest (IOException): " + ioex.Message);
			}

			await DownloadRemoteFileAsync(ct, remoteURL, localPath);
		}

		/// <summary>
		/// Gets the remote launcher version.
		/// </summary>
		/// <returns>The remote launcher version.
		/// If the version could not be retrieved from the server, a version of 0.0.0 is returned.</returns>
		protected virtual async Task<Version> GetRemoteLauncherVersionAsync(CancellationToken ct)
		{
			string remoteVersionPath = this.Config.GetLauncherVersionURL();

			// Config.GetDoOfficialUpdates is used here since the official update server always allows anonymous logins.
			string remoteVersion = await ReadRemoteFileAsync(ct, remoteVersionPath, this.Config.GetDoOfficialUpdates());

			Version version;
			if (Version.TryParse(remoteVersion, out version))
			{
				return version;
			}
			else
			{
				Log.Warn("Failed to parse the remote launcher version. Using the default of 0.0.0 instead.");
				return new Version("0.0.0");
			}
		}

		/// <summary>
		/// Gets the remote game version.
		/// </summary>
		/// <returns>The remote game version.</returns>
		protected virtual async Task<Version> GetRemoteGameVersionAsync(CancellationToken ct)
		{
			string remoteVersionPath = $"{this.Config.GetBaseProtocolURL()}/game/{this.Config.GetSystemTarget()}/bin/GameVersion.txt";
			string remoteVersion = await ReadRemoteFileAsync(ct, remoteVersionPath);

			Version version;
			if (Version.TryParse(remoteVersion, out version))
			{
				return version;
			}
			else
			{
				Log.Warn("Failed to parse the remote game version. Using the default of 0.0.0 instead.");
				return new Version("0.0.0");
			}
		}

		/// <summary>
		/// Gets the indicator label message to display to the user while repairing.
		/// </summary>
		/// <returns>The indicator label message.</returns>
		/// <param name="verifiedFiles">N files downloaded.</param>
		/// <param name="currentFilename">Current filename.</param>
		/// <param name="totalFiles">Total files to download.</param>
		protected virtual string GetVerifyIndicatorLabelMessage(string currentFilename, int verifiedFiles, int totalFiles)
		{
			return LocalizationCatalog.GetString("Verifying file {0} ({1} of {2})", currentFilename, verifiedFiles, totalFiles);
		}

		/// <summary>
		/// Gets the indicator label message to display to the user while repairing.
		/// </summary>
		/// <returns>The indicator label message.</returns>
		/// <param name="currentFilename">Current filename.</param>
		/// <param name="updatedFiles">Number of files that have been updated</param>
		/// <param name="totalFiles">Total files that are to be updated</param>
		protected virtual string GetUpdateIndicatorLabelMessage(string currentFilename, int updatedFiles, int totalFiles)
		{
			return LocalizationCatalog.GetString("Updating file {0} ({1} of {2})", currentFilename, updatedFiles, totalFiles);
		}

		/// <summary>
		/// Gets the indicator label message to display to the user while installing.
		/// </summary>
		/// <returns>The indicator label message.</returns>
		/// <param name="downloadedFiles">N files downloaded.</param>
		/// <param name="currentFilename">Current filename.</param>
		/// <param name="totalFiles">Total files to download.</param>
		protected virtual string GetDownloadIndicatorLabelMessage(string currentFilename, int downloadedFiles, int totalFiles)
		{
			return LocalizationCatalog.GetString("Downloading file {0} ({1} of {2})", currentFilename, downloadedFiles, totalFiles);
		}

		/// <summary>
		/// Gets the progress bar message.
		/// </summary>
		/// <returns>The progress bar message.</returns>
		/// <param name="filename">Filename.</param>
		/// <param name="downloadedBytes">Downloaded bytes.</param>
		/// <param name="totalBytes">Total bytes.</param>
		protected virtual string GetDownloadProgressBarMessage(string filename, long downloadedBytes, long totalBytes)
		{
			return LocalizationCatalog.GetString("Downloading {0}: {1} out of {2}", filename, downloadedBytes, totalBytes);
		}
	}
}