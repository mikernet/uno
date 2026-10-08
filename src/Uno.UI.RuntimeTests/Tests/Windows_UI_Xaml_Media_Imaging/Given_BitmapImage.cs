#if __SKIA__
#nullable enable

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Uno.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Private.Infrastructure;
using Uno.UI.RuntimeTests.Helpers;
using Windows.Storage;
using static Private.Infrastructure.TestServices;

namespace Uno.UI.RuntimeTests.Tests.Windows_UI_Xaml_Media_Imaging;

[TestClass]
[RunsOnUIThread]
public class Given_BitmapImage
{
	private const string AssetUri = "ms-appx:///Uno.UI.RuntimeTests/Assets/Transitive-ingredient01.png";

	[TestMethod]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25098")]
	public async Task When_IgnoreImageCache_Then_Not_Added_To_Cache()
	{
		// Distinct decode widths give each load its own cache key, independent of other tests.
		const int ignoredWidth = 41;
		const int cachedWidth = 42;

		var uri = new Uri(AssetUri);
		var panel = new StackPanel();
		var cacheWasEnabled = FeatureConfiguration.Image.EnableBitmapImageCache;

		try
		{
			FeatureConfiguration.Image.EnableBitmapImageCache = true;

			await UITestHelper.Load(panel, x => x.IsLoaded);

			await LoadAsync(panel, uri, ignoredWidth, BitmapCreateOptions.IgnoreImageCache);
			Assert.IsNull(await BitmapImage.GetCachedImageDataTaskForTesting(uri, ignoredWidth, null));

			await LoadAsync(panel, uri, cachedWidth, BitmapCreateOptions.None);
			var cachedTask = await BitmapImage.GetCachedImageDataTaskForTesting(uri, cachedWidth, null);
			Assert.IsNotNull(cachedTask);

			// Ignoring the cache must not replace an entry other consumers already share.
			await LoadAsync(panel, uri, cachedWidth, BitmapCreateOptions.IgnoreImageCache);
			Assert.AreSame(cachedTask, await BitmapImage.GetCachedImageDataTaskForTesting(uri, cachedWidth, null));
		}
		finally
		{
			WindowHelper.WindowContent = null;
			FeatureConfiguration.Image.EnableBitmapImageCache = cacheWasEnabled;
		}
	}

	private static async Task LoadAsync(Panel panel, Uri uri, int decodePixelWidth, BitmapCreateOptions options)
	{
		var tcs = new TaskCompletionSource<bool>();
		var bitmapImage = new BitmapImage { CreateOptions = options, DecodePixelWidth = decodePixelWidth };
		bitmapImage.ImageOpened += (_, _) => tcs.TrySetResult(true);
		bitmapImage.ImageFailed += (_, e) => tcs.TrySetException(new Exception($"Failed to load {uri}: {e.ErrorMessage}"));
		bitmapImage.UriSource = uri;

		panel.Children.Add(new Image { Width = 50, Height = 50, Source = bitmapImage });

		await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
	}

	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaDesktop)]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25096")]
	public async Task When_Shared_Download_And_First_Requester_Cancelled()
	{
		using var server = await GatedImageServer.StartAsync();

		try
		{
			var panel = await LoadPanelAsync();
			var first = new BitmapImage(server.Uri);
			var second = new BitmapImage(server.Uri);
			var firstResult = TrackOpen(first);
			var secondResult = TrackOpen(second);

			// Subscribing a loaded Image is what opens the source; both join the same cached download.
			panel.Children.Add(new Image { Width = 50, Height = 50, Source = first });
			panel.Children.Add(new Image { Width = 50, Height = 50, Source = second });

			await WindowHelper.WaitFor(() => server.RequestCount >= 1, 5000, "the download never started");

			// Cancels the first requester while the shared download is in flight (what a recycled container does).
			first.UriSource = null;

			server.ReleaseResponses();

			Assert.IsTrue(await secondResult.WaitAsync(TimeSpan.FromSeconds(10)), "The second BitmapImage should open");
			Assert.AreEqual(100, second.PixelWidth);
			Assert.AreEqual(1, server.RequestCount, "Both sources should share one download");
			Assert.IsFalse(firstResult.IsCompleted, "The cancelled BitmapImage should raise neither ImageOpened nor ImageFailed");
		}
		finally
		{
			WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaDesktop)]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25096")]
	public async Task When_Only_Requester_Cancelled_Then_Same_Uri_Reopened()
	{
		using var server = await GatedImageServer.StartAsync();

		try
		{
			var panel = await LoadPanelAsync();
			var first = new BitmapImage(server.Uri);
			panel.Children.Add(new Image { Width = 50, Height = 50, Source = first });

			await WindowHelper.WaitFor(() => server.RequestCount >= 1, 5000, "the download never started");

			first.UriSource = null;
			server.ReleaseResponses();

			var second = new BitmapImage(server.Uri);
			var secondResult = TrackOpen(second);
			panel.Children.Add(new Image { Width = 50, Height = 50, Source = second });

			Assert.IsTrue(await secondResult.WaitAsync(TimeSpan.FromSeconds(10)), "A later BitmapImage for the same Uri should open");
			Assert.AreEqual(100, second.PixelWidth);
		}
		finally
		{
			WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaDesktop)]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25096")]
	public async Task When_Shared_Download_Failed_Then_Same_Uri_Reopened()
	{
		using var server = await GatedImageServer.StartAsync();
		server.FailNextRequest = true;
		server.ReleaseResponses();

		try
		{
			var panel = await LoadPanelAsync();
			var first = new BitmapImage(server.Uri);
			var firstResult = TrackOpen(first);
			panel.Children.Add(new Image { Width = 50, Height = 50, Source = first });

			Assert.IsFalse(await firstResult.WaitAsync(TimeSpan.FromSeconds(10)), "The first BitmapImage should fail");

			// The entry is dropped by a continuation on the load, so poll rather than assume it ran before ImageFailed.
			await TestHelper.RetryAssert(
				async () => Assert.IsNull(await BitmapImage.GetCachedImageDataTaskForTesting(server.Uri, null, null), "The failed load should have been dropped from the cache"),
				count: 200);

			var second = new BitmapImage(server.Uri);
			var secondResult = TrackOpen(second);
			panel.Children.Add(new Image { Width = 50, Height = 50, Source = second });

			Assert.IsTrue(await secondResult.WaitAsync(TimeSpan.FromSeconds(10)), "A failed download must not be cached: the next BitmapImage should download again and open");
			Assert.AreEqual(100, second.PixelWidth);
			Assert.AreEqual(2, server.RequestCount, "The second BitmapImage should have downloaded again");
		}
		finally
		{
			WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaDesktop)]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25114")]
	public async Task When_Requester_Cancelled_After_Decode_Then_Abandoned_Surface_Released()
	{
		// Without the cache the load belongs to this source alone. The decode runs off the UI thread and its result is applied
		// by a dispatched continuation; a source change that lands first (a user clicking through images quickly) must release
		// the surface that continuation would otherwise drop.
		var cacheWasEnabled = FeatureConfiguration.Image.EnableBitmapImageCache;
		FeatureConfiguration.Image.EnableBitmapImageCache = false;

		try
		{
			using var server = await GatedImageServer.StartAsync();
			var bitmap = new BitmapImage(server.Uri);
			var result = TrackOpen(bitmap);
			var image = new Image { Width = 100, Height = 40, Source = bitmap };
			await UITestHelper.Load(image);

			await WindowHelper.WaitFor(() => server.RequestCount >= 1, 5000, "the download never started");

			var createdBefore = ImageData.CompositionSurfacesCreatedForTesting;
			var releasedBefore = ImageSource.ReleasedSurfacesForTesting;
			server.ReleaseResponses();

			// Holding the UI thread keeps the dispatched continuation from applying the result while the decode completes.
			var decoded = SpinWait.SpinUntil(() => ImageData.CompositionSurfacesCreatedForTesting > createdBefore, 5000);
			Assert.IsTrue(decoded, $"Pre-condition: the decode must complete (requests {server.RequestCount}, bodies sent {server.BodiesSent}, surfaces {ImageData.CompositionSurfacesCreatedForTesting - createdBefore}, opened {result.IsCompleted})");

			bitmap.UriSource = null;
			await WindowHelper.WaitForIdle();

			Assert.IsTrue(ImageSource.ReleasedSurfacesForTesting > releasedBefore, "The decoded surface nobody will show must be released");
			Assert.IsFalse(result.IsCompleted, "The cancelled BitmapImage should raise neither ImageOpened nor ImageFailed");
		}
		finally
		{
			WindowHelper.WindowContent = null;
			FeatureConfiguration.Image.EnableBitmapImageCache = cacheWasEnabled;
		}
	}

	// An Image subscribes to its source (which is what opens it) only once it is loaded.
	private static async Task<StackPanel> LoadPanelAsync()
	{
		var panel = new StackPanel();
		await UITestHelper.Load(panel, x => x.IsLoaded);
		return panel;
	}

	private static Task<bool> TrackOpen(BitmapImage image)
	{
		var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		image.ImageOpened += (_, _) => tcs.TrySetResult(true);
		image.ImageFailed += (_, _) => tcs.TrySetResult(false);
		return tcs.Task;
	}

	/// <summary>
	/// Minimal loopback HTTP server that sends the response headers immediately but holds the image bytes
	/// until <see cref="ReleaseResponses"/>, so a test can act while a download is deterministically in flight.
	/// With <see cref="FailNextRequest"/> set, the next request gets an HTTP 500 instead of the image.
	/// </summary>
	private sealed class GatedImageServer : IDisposable
	{
		private readonly TcpListener _listener;
		private readonly byte[] _body;
		private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private int _requestCount;
		private int _bodiesSent;

		private GatedImageServer(TcpListener listener, byte[] body)
		{
			_listener = listener;
			_body = body;
			Uri = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/square100.png");
			// Off the UI thread: the test may hold the UI thread while a response is pending, and awaits started from it
			// would otherwise resume through its synchronization context.
			_ = Task.Run(AcceptLoopAsync);
		}

		public static async Task<GatedImageServer> StartAsync()
		{
			var file = await StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///Assets/square100.png"));
			using var stream = await file.OpenStreamForReadAsync();
			using var memory = new MemoryStream();
			await stream.CopyToAsync(memory);

			var listener = new TcpListener(IPAddress.Loopback, 0);
			listener.Start();
			return new GatedImageServer(listener, memory.ToArray());
		}

		public Uri Uri { get; }

		public int RequestCount => Volatile.Read(ref _requestCount);

		public int BodiesSent => Volatile.Read(ref _bodiesSent);

		public bool FailNextRequest { get; set; }

		public void ReleaseResponses() => _release.TrySetResult();

		public void Dispose()
		{
			_release.TrySetResult();
			_listener.Stop();
		}

		private async Task AcceptLoopAsync()
		{
			try
			{
				while (true)
				{
					var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
					_ = HandleAsync(client);
				}
			}
			catch (ObjectDisposedException)
			{
			}
			catch (SocketException)
			{
			}
		}

		private async Task HandleAsync(TcpClient client)
		{
			try
			{
				using var _ = client;
				using var stream = client.GetStream();

				var request = new byte[8192];
				var read = 0;
				while (!Encoding.ASCII.GetString(request, 0, read).Contains("\r\n\r\n"))
				{
					var count = await stream.ReadAsync(request, read, request.Length - read).ConfigureAwait(false);
					if (count == 0)
					{
						return;
					}

					read += count;
				}

				Interlocked.Increment(ref _requestCount);

				if (FailNextRequest)
				{
					FailNextRequest = false;
					await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 500 Internal Server Error\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
					await stream.FlushAsync();
					return;
				}

				var headers = $"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {_body.Length}\r\nConnection: close\r\n\r\n";
				await stream.WriteAsync(Encoding.ASCII.GetBytes(headers)).ConfigureAwait(false);
				await stream.FlushAsync().ConfigureAwait(false);

				await _release.Task.ConfigureAwait(false);

				await stream.WriteAsync(_body).ConfigureAwait(false);
				await stream.FlushAsync().ConfigureAwait(false);
				Interlocked.Increment(ref _bodiesSent);
			}
			catch (IOException)
			{
			}
			catch (ObjectDisposedException)
			{
			}
			catch (SocketException)
			{
			}
		}
	}
}
#endif
