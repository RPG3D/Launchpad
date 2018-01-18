﻿//
//  HTTPProtocolHandler.cs
//
//  Author:
//       Jarl Gullberg <jarl.gullberg@gmail.com>
//
//  Copyright (c) 2017 Jarl Gullberg
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
//

using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;

using Launchpad.Common;
using Launchpad.Common.Enums;
using log4net;

namespace Launchpad.Launcher.Handlers.Protocols.Manifest
{
	/// <summary>
	/// HTTP protocol handler. Patches the launcher and game using the
	/// HTTP/HTTPS protocol.
	/// </summary>
	internal sealed class HTTPProtocolHandler : ManifestBasedProtocolHandler
	{
		/// <summary>
		/// Logger instance for this class.
		/// </summary>
		private static readonly ILog Log = LogManager.GetLogger(typeof(HTTPProtocolHandler));

		/// <inheritdoc />
		public override bool CanPatch()
		{
			Log.Info("Pinging remote patching server to determine if we can connect to it.");

			var canConnect = false;

			try
			{
				var plainRequest = CreateHttpWebRequest(this.Config.GetBaseHTTPUrl(), this.Config.GetRemoteUsername(), this.Config.GetRemotePassword());

				if (plainRequest == null)
				{
					return false;
				}

				plainRequest.Method = WebRequestMethods.Http.Head;
				plainRequest.Timeout = 4000;

				try
				{
					using (var response = (HttpWebResponse)plainRequest.GetResponse())
					{
						if (response.StatusCode == HttpStatusCode.OK)
						{
							canConnect = true;
						}
					}
				}
				catch (WebException wex)
				{
					Log.Warn("Unable to connect to remote patch server (WebException): " + wex.Message);
					canConnect = false;
				}
			}
			catch (WebException wex)
			{
				Log.Warn("Unable to connect due a malformed url in the configuration (WebException): " + wex.Message);
				canConnect = false;
			}

			return canConnect;
		}

		/// <inheritdoc />
		public override bool IsPlatformAvailable(ESystemTarget platform)
		{
			var remote = $"{this.Config.GetBaseHTTPUrl()}/game/{platform}/.provides";

			return DoesRemoteDirectoryOrFileExist(remote);
		}

		/// <inheritdoc />
		public override bool CanProvideChangelog()
		{
			return false;
		}

		/// <inheritdoc />
		public override string GetChangelogSource()
		{
			return string.Empty;
		}

		/// <inheritdoc />
		public override bool CanProvideBanner()
		{
			var bannerURL = $"{this.Config.GetBaseHTTPUrl()}/launcher/banner.png";

			return DoesRemoteDirectoryOrFileExist(bannerURL);
		}

		/// <inheritdoc />
		public override Bitmap GetBanner()
		{
			var bannerURL = $"{this.Config.GetBaseHTTPUrl()}/launcher/banner.png";
			var localBannerPath = $"{Path.GetTempPath()}/banner.png";

			DownloadRemoteFile(bannerURL, localBannerPath);
			return new Bitmap(localBannerPath);
		}

		/// <inheritdoc />
		protected override void DownloadRemoteFile(string url, string localPath, long totalSize = 0, long contentOffset = 0, bool useAnonymousLogin = false)
		{
			// Clean the url string
			var remoteURL = url.Replace(Path.DirectorySeparatorChar, '/');

			string username;
			string password;
			if (useAnonymousLogin)
			{
				username = "anonymous";
				password = "anonymous";
			}
			else
			{
				username = this.Config.GetRemoteUsername();
				password = this.Config.GetRemotePassword();
			}

			try
			{
				var request = CreateHttpWebRequest(remoteURL, username, password);

				request.Method = WebRequestMethods.Http.Get;
				request.AddRange(contentOffset);

				using (var contentStream = request.GetResponse().GetResponseStream())
				{
					if (contentStream == null)
					{
						Log.Error($"Failed to download the remote file at \"{remoteURL}\" (NullReferenceException from the content stream). " +
								  "Check your internet connection.");

						return;
					}

					using (var fileStream = contentOffset > 0 ? new FileStream(localPath, FileMode.Append) :
																		new FileStream(localPath, FileMode.Create))
					{
						fileStream.Position = contentOffset;
						var totalBytesDownloaded = contentOffset;

						long totalFileSize;
						if (contentStream.CanSeek)
						{
							totalFileSize = contentOffset + contentStream.Length;
						}
						else
						{
							totalFileSize = totalSize;
						}

						var bufferSize = this.Config.GetDownloadBufferSize();
						var buffer = new byte[bufferSize];

						while (true)
						{
							var bytesRead = contentStream.Read(buffer, 0, buffer.Length);

							if (bytesRead == 0)
							{
								break;
							}

							fileStream.Write(buffer, 0, bytesRead);

							totalBytesDownloaded += bytesRead;

							// Report download progress
							this.ModuleDownloadProgressArgs.ProgressBarMessage = GetDownloadProgressBarMessage
							(
								Path.GetFileName(remoteURL),
								totalBytesDownloaded,
								totalFileSize
							);
							this.ModuleDownloadProgressArgs.ProgressFraction = totalBytesDownloaded / (double)totalFileSize;
							OnModuleDownloadProgressChanged();
						}

						fileStream.Flush();
					}
				}
			}
			catch (WebException wex)
			{
				Log.Error($"Failed to download the remote file at \"{remoteURL}\" (WebException): {wex.Message}");
			}
			catch (IOException ioex)
			{
				Log.Error($"Failed to download the remote file at \"{remoteURL}\" (IOException): {ioex.Message}");
			}
		}

		/// <inheritdoc />
		protected override string ReadRemoteFile(string url, bool useAnonymousLogin = false)
		{
			var remoteURL = url.Replace(Path.DirectorySeparatorChar, '/');

			string username;
			string password;
			if (useAnonymousLogin)
			{
				username = "anonymous";
				password = "anonymous";
			}
			else
			{
				username = this.Config.GetRemoteUsername();
				password = this.Config.GetRemotePassword();
			}

			try
			{
				var request = CreateHttpWebRequest(remoteURL, username, password);

				request.Method = WebRequestMethods.Http.Get;

				var data = string.Empty;
				using (var remoteStream = request.GetResponse().GetResponseStream())
				{
					// Drop out early if the stream wasn't present
					if (remoteStream == null)
					{
						Log.Error
						(
							$"Failed to read the contents of remote file \"{remoteURL}\": " +
							"Remote stream was null. This could be due to a network interruption " +
							"or issues with the remote file."
						);

						return string.Empty;
					}

					var bufferSize = this.Config.GetDownloadBufferSize();
					var buffer = new byte[bufferSize];

					while (true)
					{
						var bytesRead = remoteStream.Read(buffer, 0, buffer.Length);

						if (bytesRead == 0)
						{
							break;
						}

						data += Encoding.UTF8.GetString(buffer, 0, bytesRead);
					}
				}

				return data.RemoveLineSeparatorsAndNulls();
			}
			catch (WebException wex)
			{
				Log.Error($"Failed to read the contents of remote file \"{remoteURL}\" (WebException): {wex.Message}");
				return string.Empty;
			}
			catch (NullReferenceException nex)
			{
				Log.Error("Failed to establish a network connection, or the connection was interrupted during the download (NullReferenceException): " + nex.Message);
				return string.Empty;
			}
		}

		/// <summary>
		/// Creates a HTTP web request.
		/// </summary>
		/// <returns>The HTTP web request.</returns>
		/// <param name="url">url of the desired remote object.</param>
		/// <param name="username">The username used for authentication.</param>
		/// <param name="password">The password used for authentication.</param>
		private static HttpWebRequest CreateHttpWebRequest(string url, string username, string password)
		{
			try
			{
				var request = (HttpWebRequest)WebRequest.Create(new Uri(url));
				request.Credentials = new NetworkCredential(username, password);

				return request;
			}
			catch (WebException wex)
			{
				Log.Warn("Unable to create a WebRequest for the specified file (WebException): " + wex.Message);
				return null;
			}
			catch (ArgumentException aex)
			{
				Log.Warn("Unable to create a WebRequest for the specified file (ArgumentException): " + aex.Message);
				return null;
			}
			catch (UriFormatException uex)
			{
				Log.Warn("Unable to create a WebRequest for the specified file (UriFormatException): " + uex.Message + "\n" +
					"You may need to add \"http://\" before the url in the config.");
				return null;
			}
		}

		/// <summary>
		/// Checks if the provided path points to a valid directory or file.
		/// </summary>
		/// <returns><c>true</c>, if the directory or file exists, <c>false</c> otherwise.</returns>
		/// <param name="url">The remote url of the directory or file.</param>
		private bool DoesRemoteDirectoryOrFileExist(string url)
		{
			var cleanURL = url.Replace(Path.DirectorySeparatorChar, '/');
			var request = CreateHttpWebRequest(cleanURL, this.Config.GetRemoteUsername(), this.Config.GetRemotePassword());

			request.Method = WebRequestMethods.Http.Head;
			HttpWebResponse response = null;
			try
			{
				response = (HttpWebResponse)request.GetResponse();
				if (response.StatusCode != HttpStatusCode.OK)
				{
					return false;
				}
			}
			catch (WebException wex)
			{
				response = (HttpWebResponse)wex.Response;
				if (response.StatusCode == HttpStatusCode.NotFound)
				{
					return false;
				}
			}
			finally
			{
				response?.Dispose();
			}

			return true;
		}
	}
}
