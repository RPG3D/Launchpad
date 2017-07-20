//
//  PatchProtocolHandler.cs
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
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using Launchpad.Common.Enums;

namespace Launchpad.Launcher.Handlers.Protocols
{
	/// <summary>
	/// Patch protocol handler.
	/// This class is the base class for all file transfer protocols, providing
	/// a common framework for protocols to adhere to. It abstracts away the actual
	/// functionality, and reduces the communication with other parts of the launcher
	/// down to requests in, files out.
	///
	/// By default, the patch protocol handler does not know anything specific about
	/// the actual workings of the protocol.
	/// </summary>
	public abstract class AsyncPatchProtocolHandler
	{
		/// <summary>
		/// Logger instance for this class.
		/// </summary>
		private static readonly ILog Log = LogManager.GetLogger(typeof(AsyncPatchProtocolHandler));

		/// <summary>
		/// TODO: Move to constructor
		/// The config handler reference.
		/// </summary>
		protected readonly ConfigHandler Config = ConfigHandler.Instance;

		/// <summary>
		/// Determines whether this instance can provide patches. Checks for an active connection to the
		/// patch provider (file server, distributed hash tables, hyperspace compression waves etc.)
		/// </summary>
		/// <returns><c>true</c> if this instance can provide patches; otherwise, <c>false</c>.</returns>
		public abstract Task<bool> CanPatchAsync(CancellationToken ct);

		/// <summary>
		/// Determines whether the protocol can provide patches and updates for the provided platform.
		/// </summary>
		/// <returns><c>true</c> if the platform is available; otherwise, <c>false</c>.</returns>
		public abstract Task<bool> IsPlatformAvailableAsync(CancellationToken ct, ESystemTarget platform);

		/// <summary>
		/// Determines whether this protocol can provide access to a changelog.
		/// </summary>
		/// <returns><c>true</c> if this protocol can provide a changelog; otherwise, <c>false</c>.</returns>
		public abstract Task<bool> CanProvideChangelogAsync(CancellationToken ct);

		/// <summary>
		/// Determines whether this protocol can provide access to a banner for the game.
		/// </summary>
		/// <returns><c>true</c> if this instance can provide banner; otherwise, <c>false</c>.</returns>
		public abstract Task<bool> CanProvideBannerAsync(CancellationToken ct);

		/// <summary>
		/// Gets the changelog.
		/// </summary>
		/// <returns>The changelog.</returns>
		public abstract Task<string> GetChangelogSourceAsync(CancellationToken ct);

		/// <summary>
		/// Gets the banner.
		/// </summary>
		/// <returns>The banner.</returns>
		public abstract Task<Bitmap> GetBannerAsync(CancellationToken ct);

		/// <summary>
		/// Determines whether or not the specified module is outdated.
		/// </summary>
		public abstract Task<bool> IsModuleOutdatedAsync(CancellationToken ct, EModule module);

		/// <summary>
		/// Installs the game.
		/// </summary>
		public virtual async Task InstallGameAsync(CancellationToken ct)
		{
			try
			{
				//create the .install file to mark that an installation has begun
				//if it exists, do nothing.
				ConfigHandler.CreateGameCookie();

				// Download Game
				await DownloadModuleAsync(ct, EModule.Game);

				// Verify Game
				await VerifyModuleAsync(ct, EModule.Game);
			}
			catch (IOException ioex)
			{
				Log.Warn("Game installation failed (IOException): " + ioex.Message);
			}
		}

		/// <summary>
		/// Verifies and repairs the files of the specified module.
		/// </summary>
		public abstract Task VerifyModuleAsync(CancellationToken ct, EModule module);

		/// <summary>
		/// Downloads the latest version of the specified module.
		/// </summary>
		protected abstract Task DownloadModuleAsync(CancellationToken ct, EModule module);

		/// <summary>
		/// Updates the specified module to the latest version.
		/// </summary>
		/// <param name="module">The module to update.</param>
		public abstract Task UpdateModuleAsync(CancellationToken ct, EModule module);
	}
}

