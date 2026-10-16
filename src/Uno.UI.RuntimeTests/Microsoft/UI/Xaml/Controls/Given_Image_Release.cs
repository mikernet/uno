#if __UNO__
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Private.Infrastructure;
using Uno.Disposables;
using Uno.UI.Composition.Drawing;
using Uno.UI.RuntimeTests.Helpers;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using static Private.Infrastructure.TestServices;

namespace Uno.UI.RuntimeTests.Tests.Windows_UI_Xaml_Controls;

[TestClass]
[RunsOnUIThread]
[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.Skia)]
public class Given_Image_Release
{
	private const string AssetUri = "ms-appx:///Assets/my500x200.jpg";
	private const string SquareAssetUri = "ms-appx:///Assets/square100.png";
	private const string TallAssetUri = "ms-appx:///Assets/test_image_100_150.png";

	[TestMethod]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25114")]
	public async Task When_Unloaded_Then_Decoded_Image_Released()
	{
		// The source owns its decoded data only when the cache does not share it between sources.
		using var _ = DisableBitmapImageCache();
		using var delay = ReleaseAfterLayoutPass();

		try
		{
			var bitmap = new BitmapImage(new Uri(AssetUri));
			var image = new Image { Width = 100, Height = 40, Source = bitmap };

			await LoadAndWaitForOpened(image);
			var surface = GetSurface(bitmap);
			Assert.IsNotNull(surface?.Image, "Pre-condition: the bitmap is decoded while an Image shows it");

			WindowHelper.WindowContent = null;
			await WindowHelper.WaitForIdle();
			Assert.IsNull(surface.Image, "Nothing displays the bitmap once its only Image is unloaded, so its decoded pixels must be released");
			Assert.IsFalse(bitmap.IsOpened, "The source must not keep data it released");

			// Showing it again decodes it again.
			await UITestHelper.Load(image);
			await WindowHelper.WaitFor(() => bitmap.IsOpened, 10000, "An Image that is loaded again must decode its source again");
			Assert.IsNotNull(GetSurface(bitmap)?.Image);
		}
		finally
		{
			WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25114")]
	public async Task When_Decoded_Again_After_Release_Then_ImageOpened_Raised_Once()
	{
		// WinUI raises ImageOpened once per source; decoding the same source again after its release is not a new open.
		using var _ = DisableBitmapImageCache();
		using var delay = ReleaseAfterLayoutPass();

		try
		{
			var bitmap = new BitmapImage(new Uri(AssetUri));
			var image = new Image { Width = 100, Height = 40, Source = bitmap };
			var bitmapOpened = 0;
			var imageOpened = 0;
			bitmap.ImageOpened += (_, _) => bitmapOpened++;
			image.ImageOpened += (_, _) => imageOpened++;

			await LoadAndWaitForOpened(image);

			WindowHelper.WindowContent = null;
			await WindowHelper.WaitForIdle();
			Assert.IsFalse(bitmap.IsOpened, "Pre-condition: the decoded data is released while nothing displays it");

			await UITestHelper.Load(image);
			await WindowHelper.WaitFor(() => bitmap.IsOpened, 10000, "Pre-condition: the bitmap is decoded again");
			await WindowHelper.WaitForIdle();

			Assert.AreEqual(1, bitmapOpened, "BitmapImage.ImageOpened is raised once per source");
			Assert.AreEqual(1, imageOpened, "Image.ImageOpened is raised once per source");

			// A new source is a new open.
			var opened = TrackOpened(image);
			bitmap.UriSource = new Uri(SquareAssetUri);
			await opened.WaitAsync(TimeSpan.FromSeconds(10));
			Assert.AreEqual(2, bitmapOpened, "A new UriSource raises BitmapImage.ImageOpened again");
			Assert.AreEqual(2, imageOpened, "A new UriSource raises Image.ImageOpened again");
		}
		finally
		{
			WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25114")]
	public async Task When_Moved_Before_Next_Layout_Pass_Then_Decoded_Image_Kept()
	{
		// Moving an Image to another parent unloads it at once and loads it again in the next layout pass: even with a limit
		// of 0, which releases what nothing displays at the end of that pass, it must keep its pixels rather than release
		// them and decode them again.
		using var _ = DisableBitmapImageCache();
		using var delay = ReleaseAfterLayoutPass();
		using var decoder = TrackingDecoder.Install();

		try
		{
			var first = new StackPanel();
			var second = new StackPanel();
			await UITestHelper.Load(new StackPanel { Children = { first, second } }, x => x.IsLoaded);

			var bitmap = new BitmapImage(new Uri(AssetUri));
			var image = new Image { Width = 100, Height = 40, Source = bitmap };
			var bitmapOpened = 0;
			var imageOpened = 0;
			bitmap.ImageOpened += (_, _) => bitmapOpened++;
			image.ImageOpened += (_, _) => imageOpened++;

			await AddAndWaitForOpened(first, image);
			await WindowHelper.WaitForIdle();
			var surface = GetSurface(bitmap);
			var decodes = decoder.Count(500, 200);

			first.Children.Remove(image);
			second.Children.Add(image);
			await WindowHelper.WaitForIdle();

			Assert.IsTrue(image.IsLoaded);
			Assert.IsNotNull(surface.Image, "An Image that re-enters the tree within the pass keeps its decoded pixels");
			Assert.AreSame(surface, GetSurface(bitmap), "The source must deliver the data it kept");
			Assert.AreEqual(decodes, decoder.Count(500, 200), "Nothing must be decoded again");
			Assert.AreEqual(1, bitmapOpened);
			Assert.AreEqual(1, imageOpened);
		}
		finally
		{
			WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25114")]
	public async Task When_Cached_Image_Unloaded_Then_Shown_Again_Without_Reopen()
	{
		// A result shared through the bitmap cache is the cache's, so its source keeps it: an Image that leaves the tree
		// and comes back is shown again at once, without opening its source again.
		using var _ = EnableBitmapImageCache();
		using var delay = ReleaseAfterLayoutPass();
		using var decoder = TrackingDecoder.Install();

		try
		{
			var bitmap = new BitmapImage(new Uri(TallAssetUri));
			var image = new Image { Width = 100, Height = 40, Source = bitmap };
			var bitmapOpened = 0;
			var imageOpened = 0;
			bitmap.ImageOpened += (_, _) => bitmapOpened++;
			image.ImageOpened += (_, _) => imageOpened++;

			await LoadAndWaitForOpened(image);
			var surface = GetSurface(bitmap);
			var decodes = decoder.Count(100, 150);

			WindowHelper.WindowContent = null;
			await WindowHelper.WaitForIdle();
			Assert.IsTrue(bitmap.IsOpened, "A source keeps the data it shares through the cache");
			Assert.IsNotNull(surface.Image, "A shared surface is the cache's to release");

			await UITestHelper.Load(image);
			Assert.AreSame(surface, GetSurface(bitmap));
			Assert.AreEqual(decodes, decoder.Count(100, 150), "Nothing must be decoded again");
			Assert.AreEqual(1, bitmapOpened, "The source must not be opened again");
			Assert.AreEqual(1, imageOpened);
		}
		finally
		{
			WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25114")]
	public async Task When_Source_Replaced_Then_Previous_Decoded_Image_Released()
	{
		using var _ = DisableBitmapImageCache();
		using var delay = ReleaseAfterLayoutPass();

		try
		{
			var first = new BitmapImage(new Uri(AssetUri));
			var image = new Image { Width = 100, Height = 40, Source = first };

			await LoadAndWaitForOpened(image);
			var surface = GetSurface(first);

			var opened = TrackOpened(image);
			var second = new BitmapImage(new Uri(SquareAssetUri));
			image.Source = second;
			await opened.WaitAsync(TimeSpan.FromSeconds(10));
			await WindowHelper.WaitForIdle();

			Assert.IsNull(surface.Image, "The replaced bitmap is shown by nothing, so its decoded pixels must be released");
			Assert.IsNotNull(GetSurface(second)?.Image);
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
		using var _ = DisableBitmapImageCache();
		using var decoder = TrackingDecoder.Install();

		try
		{
			using var server = await GatedImageServer.StartAsync();
			var bitmap = new BitmapImage(server.Uri);
			var result = TrackOpen(bitmap);
			var image = new Image { Width = 100, Height = 40, Source = bitmap };
			await UITestHelper.Load(image);

			await WindowHelper.WaitFor(() => server.RequestCount >= 1, 5000, "the download never started");

			server.ReleaseResponses();

			// Holding the UI thread keeps the dispatched continuation from applying the result while the decode completes.
			var decoded = SpinWait.SpinUntil(() => decoder.Count(100, 100) > 0, 5000);
			Assert.IsTrue(decoded, $"Pre-condition: the decode must complete (requests {server.RequestCount}, bodies sent {server.BodiesSent}, opened {result.IsCompleted})");

			bitmap.UriSource = null;
			await WindowHelper.WaitForIdle();

			var abandoned = decoder.Decoded(100, 100).Single();
			await TestHelper.RetryAssert(() => Assert.IsTrue(abandoned.IsReleased, "The decoded surface nobody will show must be released"));
			Assert.IsFalse(result.IsCompleted, "The cancelled BitmapImage should raise neither ImageOpened nor ImageFailed");
		}
		finally
		{
			WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25114")]
	public async Task When_UriSource_Changed_Then_Previous_Decoded_Image_Released()
	{
		using var _ = DisableBitmapImageCache();

		try
		{
			var bitmap = new BitmapImage(new Uri(AssetUri));
			var image = new Image { Width = 100, Height = 40, Source = bitmap };

			await LoadAndWaitForOpened(image);
			var surface = GetSurface(bitmap);

			var opened = TrackOpened(image);
			bitmap.UriSource = new Uri(SquareAssetUri);
			Assert.IsNotNull(surface.Image, "The Image keeps showing the previous image until the new one is decoded");

			await opened.WaitAsync(TimeSpan.FromSeconds(10));
			await WindowHelper.WaitForIdle();

			Assert.IsNull(surface.Image, "The surface decoded for the previous UriSource is shown by nothing once the new one is, so it must be released");
			Assert.IsTrue(bitmap.IsOpened, "The bitmap must open its new UriSource");
			Assert.AreEqual(100, bitmap.PixelWidth);
		}
		finally
		{
			WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25114")]
	public async Task When_Source_Changed_While_Unloaded_Then_Decoded_On_Load()
	{
		using var _ = DisableBitmapImageCache();
		using var decoder = TrackingDecoder.Install();

		try
		{
			var image = new Image { Width = 100, Height = 40, Source = new BitmapImage(new Uri(AssetUri)) };
			await LoadAndWaitForOpened(image);

			WindowHelper.WindowContent = null;
			await WindowHelper.WaitForIdle();

			// A recycled container gets its new source while it is off the tree: nothing decodes until it is loaded again.
			var bitmap = new BitmapImage(new Uri(SquareAssetUri));
			image.Source = bitmap;
			await Task.Delay(200);
			await WindowHelper.WaitForIdle();

			Assert.IsFalse(bitmap.IsOpened, "An unloaded Image must not decode its source");
			Assert.AreEqual(0, decoder.Count(100, 100), "An unloaded Image must not decode its source");

			await LoadAndWaitForOpened(image);
			Assert.IsTrue(bitmap.IsOpened, "The Image decodes its source once it is loaded");
			Assert.AreEqual(1, decoder.Count(100, 100), "The Image decodes its source once it is loaded");
		}
		finally
		{
			WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaDesktop)]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25114")]
	public async Task When_Stream_Load_Superseded_After_Decode_Then_Abandoned_Surface_Released()
	{
		// A stream load is never cached, so its result is the source's alone. The decode runs off the UI thread and its
		// result is applied by a dispatched continuation; a second SetSourceAsync that lands first must release the
		// surface of the first, which must not raise ImageOpened for a stream the source no longer has.
		var square = await ReadAssetAsync(SquareAssetUri);
		var tall = await ReadAssetAsync(TallAssetUri);
		using var decoder = TrackingDecoder.Install();

		try
		{
			var bitmap = new BitmapImage();
			var image = new Image { Width = 100, Height = 40, Source = bitmap };
			await UITestHelper.Load(image);

			var openedCount = 0;
			var failedCount = 0;
			bitmap.ImageOpened += (_, _) => openedCount++;
			bitmap.ImageFailed += (_, _) => failedCount++;

			// SetSourceAsync reads the pixel size with a decode of its own, on the calling thread, then decodes the stream
			// for the source off the UI thread.
			var first = bitmap.SetSourceAsync(new MemoryStream(square));

			// Holding the UI thread keeps the dispatched continuation from applying the result while the decode completes.
			var decoded = SpinWait.SpinUntil(() => decoder.Count(100, 100) >= 2, 5000);
			Assert.IsTrue(decoded, "Pre-condition: the first decode must complete");

			var second = bitmap.SetSourceAsync(new MemoryStream(tall));
			await second.WaitAsync(TimeSpan.FromSeconds(10));
			await first.WaitAsync(TimeSpan.FromSeconds(10));
			await WindowHelper.WaitForIdle();

			Assert.IsTrue(decoder.Decoded(100, 100).All(d => d.IsReleased), "The surface decoded for the superseded stream is shown by nothing, so it must be released");
			Assert.IsTrue(decoder.Decoded(100, 150).Any(d => !d.IsReleased), "The surface of the stream the source has must be kept");
			Assert.AreEqual(1, openedCount, "Only the stream the source still has may raise ImageOpened");
			Assert.AreEqual(0, failedCount, "The superseded load must not raise ImageFailed");
			Assert.AreEqual(150, bitmap.PixelHeight);
		}
		finally
		{
			WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25114")]
	public async Task When_Stream_Disposed_After_SetSourceAsync_Then_Decoded_Again_On_Reload()
	{
		// The source clones the stream it is given, so decoding it again after a release does not need the app's stream.
		var square = await ReadAssetAsync(SquareAssetUri);
		using var delay = ReleaseAfterLayoutPass();

		try
		{
			var bitmap = new BitmapImage();
			var stream = new MemoryStream(square);
			await bitmap.SetSourceAsync(stream);
			stream.Dispose();

			var image = new Image { Width = 100, Height = 40, Source = bitmap };
			await LoadAndWaitForOpened(image);
			var surface = GetSurface(bitmap);

			WindowHelper.WindowContent = null;
			await WindowHelper.WaitForIdle();
			Assert.IsNull(surface.Image, "Pre-condition: the decoded pixels are released while nothing displays them");

			await UITestHelper.Load(image);
			await WindowHelper.WaitFor(() => bitmap.IsOpened, 10000, "The stream must be decoded again once an Image shows it");
			Assert.IsNotNull(GetSurface(bitmap)?.Image);
			Assert.AreEqual(100, bitmap.PixelWidth);
		}
		finally
		{
			WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25114")]
	public async Task When_Cache_Disabled_Then_Cached_Surface_Not_Released()
	{
		var cacheWasEnabled = FeatureConfiguration.Image.EnableBitmapImageCache;
		var uri = new Uri(TallAssetUri);
		using var delay = ReleaseAfterLayoutPass();
		using var decoder = TrackingDecoder.Install();

		try
		{
			var panel = new StackPanel();
			await UITestHelper.Load(panel, x => x.IsLoaded);

			// A cache-on requester fills the cache and a second one shares the entry: unloading both must not release
			// the shared surface, which is the cache's (an entry is only evicted when its load fails).
			FeatureConfiguration.Image.EnableBitmapImageCache = true;
			var firstBitmap = new BitmapImage(uri);
			var first = new Image { Width = 100, Height = 40, Source = firstBitmap };
			await AddAndWaitForOpened(panel, first);
			var cachedSurface = GetSurface(firstBitmap);

			var decodes = decoder.Count(100, 150);
			var sharingBitmap = new BitmapImage(uri);
			var sharing = new Image { Width = 100, Height = 40, Source = sharingBitmap };
			await AddAndWaitForOpened(panel, sharing);
			Assert.AreEqual(decodes, decoder.Count(100, 150), "A cache-on requester is served from the cache");
			Assert.AreSame(cachedSurface, GetSurface(sharingBitmap));

			panel.Children.Remove(sharing);
			panel.Children.Remove(first);
			await WindowHelper.WaitForIdle();
			Assert.IsNotNull(cachedSurface.Image, "A shared surface is the cache's to release, not a requester's");

			// With the cache off a source decodes for itself even when an entry exists, and releases only its own surface.
			FeatureConfiguration.Image.EnableBitmapImageCache = false;
			var ownBitmap = new BitmapImage(uri);
			var own = new Image { Width = 100, Height = 40, Source = ownBitmap };
			await AddAndWaitForOpened(panel, own);
			var ownSurface = GetSurface(ownBitmap);
			Assert.AreNotSame(cachedSurface, ownSurface, "A cache-off source must not be served from the cache");
			Assert.AreEqual(decodes + 1, decoder.Count(100, 150), "A cache-off source decodes for itself");

			panel.Children.Remove(own);
			await WindowHelper.WaitForIdle();
			Assert.IsNull(ownSurface.Image, "A cache-off source owns its surface and releases it when unloaded");
			Assert.IsNotNull(cachedSurface.Image, "Releasing an owned surface must leave the cached one alone");

			// The cached surface is intact: a later cache-on requester opens without decoding again.
			FeatureConfiguration.Image.EnableBitmapImageCache = true;
			var cached = new BitmapImage(uri);
			await AddAndWaitForOpened(panel, new Image { Width = 100, Height = 40, Source = cached });
			Assert.AreEqual(decodes + 1, decoder.Count(100, 150), "The cached surface must still serve a later requester");
			Assert.AreSame(cachedSurface, GetSurface(cached));
			Assert.AreEqual(150, cached.PixelHeight);
		}
		finally
		{
			WindowHelper.WindowContent = null;
			FeatureConfiguration.Image.EnableBitmapImageCache = cacheWasEnabled;
		}
	}

	[TestMethod]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25114")]
	public async Task When_Disposed_While_Displayed_Then_Opened_Again()
	{
		// Dispose drops what the source decoded, but an Image still showing it must not be left without an image: it keeps
		// the previous pixels until the source is opened again for it.
		using var _ = DisableBitmapImageCache();
		using var delay = ReleaseAfterLayoutPass();

		try
		{
			var bitmap = new BitmapImage(new Uri(AssetUri));
			var image = new Image { Width = 100, Height = 40, Source = bitmap };
			await LoadAndWaitForOpened(image);
			var surface = GetSurface(bitmap);

			bitmap.Dispose();
			Assert.IsNotNull(surface.Image, "The Image keeps showing the previous pixels until the source is opened again");

			await WindowHelper.WaitFor(() => bitmap.IsOpened, 10000, "The source must be opened again for the Image that still shows it");
			await WindowHelper.WaitForIdle();

			var reopened = GetSurface(bitmap);
			Assert.AreNotSame(surface, reopened);
			Assert.IsNotNull(reopened?.Image);
			Assert.IsNull(surface.Image, "The previous pixels are released once the Image shows the new ones");
		}
		finally
		{
			WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaDesktop)]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25114")]
	public async Task When_Disposed_While_Downloading_Then_Subscriber_Still_Opened()
	{
		// Dispose cancels the open in flight; an Image still showing the source must get it from a new one.
		using var _ = DisableBitmapImageCache();

		try
		{
			using var server = await GatedImageServer.StartAsync();
			var bitmap = new BitmapImage(server.Uri);
			var image = new Image { Width = 100, Height = 40, Source = bitmap };
			await UITestHelper.Load(image);
			await WindowHelper.WaitFor(() => server.RequestCount >= 1, 5000, "the download never started");

			bitmap.Dispose();
			server.ReleaseResponses();

			await WindowHelper.WaitFor(() => bitmap.IsOpened, 10000, "The Image that still shows the source must get its image");
			Assert.AreEqual(100, bitmap.PixelWidth);
		}
		finally
		{
			WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25114")]
	public async Task When_Reentered_Within_Release_Delay_Then_Decoded_Image_Kept()
	{
		// Within the limit, an Image that comes back before the release delay ends keeps its pixels.
		using var _ = DisableBitmapImageCache();
		using var limit = SetMaxUndisplayedImageBytes(long.MaxValue);
		using var delay = SetReleaseDelay(TimeSpan.FromSeconds(10));
		using var decoder = TrackingDecoder.Install();

		try
		{
			var bitmap = new BitmapImage(new Uri(AssetUri));
			var image = new Image { Width = 100, Height = 40, Source = bitmap };
			var bitmapOpened = 0;
			var imageOpened = 0;
			bitmap.ImageOpened += (_, _) => bitmapOpened++;
			image.ImageOpened += (_, _) => imageOpened++;

			await LoadAndWaitForOpened(image);
			var surface = GetSurface(bitmap);
			var decodes = decoder.Count(500, 200);

			WindowHelper.WindowContent = null;
			await WindowHelper.WaitForIdle();
			Assert.IsTrue(bitmap.IsOpened, "The source keeps its data within the release delay");
			Assert.IsNotNull(surface.Image, "The source keeps its pixels within the release delay");

			await UITestHelper.Load(image);
			Assert.AreSame(surface, GetSurface(bitmap));
			Assert.AreEqual(decodes, decoder.Count(500, 200), "Nothing must be decoded again");
			Assert.AreEqual(1, bitmapOpened);
			Assert.AreEqual(1, imageOpened);
		}
		finally
		{
			WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25114")]
	public async Task When_Unused_Past_Release_Delay_Then_Released()
	{
		var releaseDelay = TimeSpan.FromMilliseconds(200);
		using var _ = DisableBitmapImageCache();
		using var limit = SetMaxUndisplayedImageBytes(long.MaxValue);
		using var delay = SetReleaseDelay(releaseDelay);

		try
		{
			var bitmap = new BitmapImage(new Uri(AssetUri));
			var image = new Image { Width = 100, Height = 40, Source = bitmap };
			await LoadAndWaitForOpened(image);
			var surface = GetSurface(bitmap);

			var unloaded = Stopwatch.StartNew();
			WindowHelper.WindowContent = null;
			Assert.IsNotNull(surface.Image, "Nothing is released before the delay");

			await WindowHelper.WaitFor(() => surface.Image is null, 5000, "The decoded pixels must be released once nothing has displayed them for the delay");
			Assert.IsTrue(unloaded.Elapsed >= releaseDelay - TimeSpan.FromMilliseconds(10), $"Released after {unloaded.Elapsed}, before the delay");
			Assert.IsFalse(bitmap.IsOpened);
		}
		finally
		{
			WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25114")]
	public async Task When_Undisplayed_Images_Exceed_Limit_Then_Oldest_Released_First()
	{
		// The limit is set just under what the three images hold, so that releasing the oldest is enough.
		using var _ = DisableBitmapImageCache();
		using var limit = SetMaxUndisplayedImageBytes(long.MaxValue);
		using var delay = SetReleaseDelay(TimeSpan.FromSeconds(10));

		try
		{
			var panel = new StackPanel();
			await UITestHelper.Load(panel, x => x.IsLoaded);

			var bitmaps = new BitmapImage[3];
			var images = new Image[3];
			for (var i = 0; i < 3; i++)
			{
				bitmaps[i] = new BitmapImage(new Uri(AssetUri));
				images[i] = new Image { Width = 100, Height = 40, Source = bitmaps[i] };
				await AddAndWaitForOpened(panel, images[i]);
			}

			var surfaces = bitmaps.Select(GetSurface).ToArray();
			Assert.IsTrue(surfaces.All(s => s.DecodedByteCount >= 500 * 200 * 4), "Pre-condition: a decode counts its pixels");

			// Measured in the same turn as the removals, so no texture is drawn in between and the sizes are those that wait.
			FeatureConfiguration.Image.MaxUndisplayedImageBytes = surfaces.Sum(s => s.DecodedByteCount) - 1;
			foreach (var image in images)
			{
				panel.Children.Remove(image);
			}

			Assert.IsNotNull(surfaces[0].Image, "Nothing is released before the layout pass");
			await WindowHelper.WaitForIdle();

			Assert.IsNull(surfaces[0].Image, "The image that waits the longest is released first once the waiting images exceed the limit");
			Assert.IsNotNull(surfaces[1].Image, "Images within the limit keep their pixels");
			Assert.IsNotNull(surfaces[2].Image, "Images within the limit keep their pixels");
		}
		finally
		{
			WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25114")]
	public async Task When_Moved_Before_Next_Layout_Pass_Over_Limit_Then_Decoded_Image_Kept()
	{
		// An image larger than the limit by itself is released at the end of the next layout pass, so one moved before that
		// pass keeps its pixels.
		using var _ = DisableBitmapImageCache();
		using var limit = SetMaxUndisplayedImageBytes(100_000);
		using var delay = SetReleaseDelay(TimeSpan.FromSeconds(10));

		try
		{
			var first = new StackPanel();
			var second = new StackPanel();
			await UITestHelper.Load(new StackPanel { Children = { first, second } }, x => x.IsLoaded);

			var bitmap = new BitmapImage(new Uri(AssetUri));
			var image = new Image { Width = 100, Height = 40, Source = bitmap };
			await AddAndWaitForOpened(first, image);
			var surface = GetSurface(bitmap);

			first.Children.Remove(image);
			second.Children.Add(image);
			await WindowHelper.WaitForIdle();
			Assert.IsNotNull(surface.Image, "An Image that re-enters the tree within the pass keeps its decoded pixels");

			second.Children.Remove(image);
			await WindowHelper.WaitForIdle();
			Assert.IsNull(surface.Image, "An image over the limit by itself is released after the pass once it stays away");
		}
		finally
		{
			WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25114")]
	public async Task When_Moved_During_Layout_After_Loaded_Then_Decoded_Image_Kept()
	{
		// An Image moved by the layout that follows Loaded within a pass is only loaded again by the next pass, so even with
		// a limit of 0 its pixels must wait for that pass. A Rendering handler adds an element, so the pass raises Loaded a
		// second time; that element's Loaded invalidates a control whose measure, in the layout run after it, moves the Image.
		using var _ = DisableBitmapImageCache();
		using var delay = ReleaseAfterLayoutPass();
		using var decoder = TrackingDecoder.Install();

		try
		{
			var first = new StackPanel();
			var second = new StackPanel();
			var hook = new MeasureHook();
			var root = new StackPanel { Children = { first, second, hook } };
			await UITestHelper.Load(root, x => x.IsLoaded);

			var bitmap = new BitmapImage(new Uri(AssetUri));
			var image = new Image { Width = 100, Height = 40, Source = bitmap };
			await AddAndWaitForOpened(first, image);
			await WindowHelper.WaitForIdle();
			var surface = GetSurface(bitmap);
			var decodes = decoder.Count(500, 200);

			var armed = false;
			var moved = false;
			hook.Measuring = () =>
			{
				if (armed && !moved)
				{
					moved = true;
					first.Children.Remove(image);
					second.Children.Add(image);
				}
			};

			var marker = new Border { Width = 1, Height = 1 };
			marker.Loaded += (_, _) =>
			{
				armed = true;
				hook.InvalidateMeasure();
			};

			EventHandler<object> rendering = null;
			rendering = (_, _) =>
			{
				Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= rendering;
				root.Children.Add(marker);
			};
			Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += rendering;

			await WindowHelper.WaitFor(() => moved, 5000, "Pre-condition: the Image must be moved by the layout after Loaded");
			await WindowHelper.WaitFor(() => image.IsLoaded, 5000);
			await WindowHelper.WaitForIdle();

			Assert.IsNotNull(surface.Image, "An Image moved during the pass keeps its decoded pixels until the next pass loads it");
			Assert.AreSame(surface, GetSurface(bitmap));
			Assert.AreEqual(decodes, decoder.Count(500, 200), "Nothing must be decoded again");
		}
		finally
		{
			WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaDesktop)]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25114")]
	public async Task When_RenderTargetBitmap_Open_Superseded_Then_Abandoned_Surface_Released()
	{
		// A RenderTargetBitmap builds its surface off the UI thread and applies it in a dispatched continuation; an open
		// superseded before that (a new render of a live preview, or Dispose) must still release the surface it built.
		using var decoder = TrackingDecoder.Install();

		try
		{
			var content = new Border { Width = 20, Height = 20, Background = new SolidColorBrush(Microsoft.UI.Colors.Red) };
			var bitmap = new RenderTargetBitmap();
			var image = new Image { Width = 20, Height = 20, Source = bitmap };
			await UITestHelper.Load(new StackPanel { Children = { content, image } }, x => x.IsLoaded);

			await bitmap.RenderAsync(content);
			await WindowHelper.WaitFor(() => bitmap.IsOpened, 5000, "Pre-condition: the render is shown");

			// Dispose opens the bitmap again for the Image that shows it.
			var before = decoder.All.Length;
			bitmap.Dispose();

			// Holding the UI thread keeps the dispatched continuation from applying the result while the open completes.
			Assert.IsTrue(SpinWait.SpinUntil(() => decoder.All.Length > before, 5000), "Pre-condition: the open must build its surface");
			var abandoned = decoder.All[before];

			bitmap.Dispose();
			await WindowHelper.WaitFor(() => bitmap.IsOpened, 5000, "The second open must deliver");
			await WindowHelper.WaitForIdle();

			await TestHelper.RetryAssert(() => Assert.IsTrue(abandoned.IsReleased, "The surface built by the superseded open must be released"));
		}
		finally
		{
			WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25114")]
	public async Task When_Source_Fails_Then_Previous_Image_Not_Shown()
	{
		// WinUI drops the surfaces of a source that failed to open, so the Image shows nothing rather than its previous
		// image, whether that image was shared through the cache (and stays alive) or owned (and released).
		await AssertFailureClearsImage(cacheEnabled: true);
		await AssertFailureClearsImage(cacheEnabled: false);

		static async Task AssertFailureClearsImage(bool cacheEnabled)
		{
			using var cache = SetBitmapImageCache(cacheEnabled);
			using var delay = ReleaseAfterLayoutPass();

			try
			{
				var bitmap = new BitmapImage(new Uri(AssetUri));
				var image = new Image { Source = bitmap };
				var panel = new StackPanel { Orientation = Orientation.Horizontal, Height = 40 };
				await UITestHelper.Load(panel, x => x.IsLoaded);
				await AddAndWaitForOpened(panel, image);
				await WindowHelper.WaitForIdle();
				var surface = GetSurface(bitmap);
				Assert.IsTrue(image.ActualWidth > 0, "Pre-condition: the Image shows its source");

				var failed = new TaskCompletionSource();
				image.ImageFailed += (_, _) => failed.TrySetResult();
				bitmap.UriSource = new Uri("ms-appx:///Assets/does-not-exist.png");
				await failed.Task.WaitAsync(TimeSpan.FromSeconds(10));
				await WindowHelper.WaitForIdle();

				Assert.AreEqual(0, image.ActualWidth, $"The Image must not show its previous image once its source failed (cache {(cacheEnabled ? "on" : "off")})");
				if (!cacheEnabled)
				{
					Assert.IsNull(surface.Image, "The source releases the image it owned");
				}
			}
			finally
			{
				WindowHelper.WindowContent = null;
			}
		}
	}

	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaDesktop)]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25114")]
	public async Task When_Source_Changed_During_Notification_Then_Skipped_Subscriber_Keeps_Its_Pixels()
	{
		// An ImageBrush notified before an Image changes the source again from its ImageOpened, to one that is still
		// downloading: the notification stops before the Image, which still shows the first image, so that one must stay
		// until the third arrives. The second image is a stream: a UriSource change would subscribe the brush again, last.
		var square = await ReadAssetAsync(SquareAssetUri);
		var tall = await ReadAssetAsync(TallAssetUri);
		using var _ = DisableBitmapImageCache();
		using var delay = ReleaseAfterLayoutPass();

		try
		{
			using var server = await GatedImageServer.StartAsync();
			var bitmap = new BitmapImage();
			var brush = new ImageBrush { ImageSource = bitmap };
			var image = new Image { Width = 100, Height = 40, Source = bitmap };
			await UITestHelper.Load(image);

			var opened = TrackOpened(image);
			await bitmap.SetSourceAsync(new MemoryStream(square));
			await opened.WaitAsync(TimeSpan.FromSeconds(10));
			await WindowHelper.WaitForIdle();
			var first = GetSurface(bitmap);

			var armed = true;
			brush.ImageOpened += (_, _) =>
			{
				if (armed)
				{
					armed = false;
					bitmap.UriSource = server.Uri;
				}
			};

			var second = bitmap.SetSourceAsync(new MemoryStream(tall));
			await WindowHelper.WaitFor(() => !armed, 10000, "Pre-condition: the brush gets the second image and changes the source");
			await WindowHelper.WaitFor(() => server.RequestCount >= 1, 5000, "Pre-condition: the third image is downloading");
			await WindowHelper.WaitForIdle();

			Assert.IsNotNull(first.Image, "The Image the notification skipped still shows the first image, which must stay");

			opened = TrackOpened(image);
			server.ReleaseResponses();
			await opened.WaitAsync(TimeSpan.FromSeconds(10));
			await WindowHelper.WaitForIdle();

			Assert.IsNull(first.Image, "Once every subscriber has the third image, the first is released");
			GC.KeepAlive(brush);
		}
		finally
		{
			WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25114")]
	public async Task When_Source_Changed_During_Successive_Notifications_Then_Only_Shown_Pixels_Kept()
	{
		// A brush notified before an Image sets the next stream from each ImageOpened, so every notification stops before
		// the Image: the Image keeps showing the first image throughout, while the brush moves through the others, and
		// each image the brush moved past must go.
		var square = await ReadAssetAsync(SquareAssetUri);
		var tall = await ReadAssetAsync(TallAssetUri);
		using var delay = ReleaseAfterLayoutPass();
		using var decoder = TrackingDecoder.Install();

		try
		{
			var bitmap = new BitmapImage();
			var brush = new ImageBrush { ImageSource = bitmap };
			var image = new Image { Width = 100, Height = 40, Source = bitmap };
			await UITestHelper.Load(image);

			var opened = TrackOpened(image);
			await bitmap.SetSourceAsync(new MemoryStream(square));
			await opened.WaitAsync(TimeSpan.FromSeconds(10));
			await WindowHelper.WaitForIdle();
			var first = GetSurface(bitmap);

			var remaining = 6;
			var mostKept = 0;
			var firstKept = true;
			var done = new TaskCompletionSource();
			brush.ImageOpened += (_, _) =>
			{
				mostKept = Math.Max(mostKept, decoder.All.Count(d => !d.IsReleased));
				firstKept &= first.Image is not null;

				if (remaining-- > 0)
				{
					_ = bitmap.SetSourceAsync(new MemoryStream(remaining % 2 == 0 ? square : tall));
				}
				else
				{
					done.TrySetResult();
				}
			};

			_ = bitmap.SetSourceAsync(new MemoryStream(tall));
			await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
			await WindowHelper.WaitForIdle();

			Assert.IsTrue(firstKept, "The Image, skipped by every notification, still shows the first image, which must stay");
			Assert.IsTrue(mostKept <= 4, $"Only the images still shown (and one being replaced) may be kept, not {mostKept}");
			Assert.IsNull(first.Image, "Once the Image has the last image, the first is released");
			Assert.IsTrue(decoder.All.Count(d => !d.IsReleased) <= 1, "Only the image shown is kept at the end");
			GC.KeepAlive(brush);
		}
		finally
		{
			WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaDesktop)]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25114")]
	public async Task When_Source_Changed_By_Last_Subscriber_Then_Previous_Pixels_Released()
	{
		// The same change made by the last subscriber skips nobody: every subscriber shows the second image, so the first
		// goes after the layout pass, without waiting for the third.
		using var _ = DisableBitmapImageCache();
		using var delay = ReleaseAfterLayoutPass();

		try
		{
			using var server = await GatedImageServer.StartAsync();
			var bitmap = new BitmapImage(new Uri(AssetUri));
			var image = new Image { Width = 100, Height = 40, Source = bitmap };
			await LoadAndWaitForOpened(image);
			await WindowHelper.WaitForIdle();
			var first = GetSurface(bitmap);
			var brush = new ImageBrush { ImageSource = bitmap };

			var armed = true;
			brush.ImageOpened += (_, _) =>
			{
				if (armed)
				{
					armed = false;
					bitmap.UriSource = server.Uri;
				}
			};

			bitmap.UriSource = new Uri(TallAssetUri);
			await WindowHelper.WaitFor(() => !armed, 10000, "Pre-condition: the brush gets the second image and changes the source");
			await WindowHelper.WaitFor(() => server.RequestCount >= 1, 5000, "Pre-condition: the third image is downloading");
			await WindowHelper.WaitForIdle();

			Assert.IsNull(first.Image, "No subscriber shows the first image anymore, so it must not wait for the third");
			GC.KeepAlive(brush);
		}
		finally
		{
			WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25114")]
	public async Task When_Stream_Set_Again_From_Its_Completion_Then_Previous_Frames_Released()
	{
		// A preview that sets the next frame as soon as SetSourceAsync completes runs inside the notification of the
		// frame it awaited: each change is made by the last subscriber, so the frames shown before must still go.
		var square = await ReadAssetAsync(SquareAssetUri);
		var tall = await ReadAssetAsync(TallAssetUri);
		using var delay = ReleaseAfterLayoutPass();
		using var decoder = TrackingDecoder.Install();

		try
		{
			var bitmap = new BitmapImage();
			var image = new Image { Width = 100, Height = 40, Source = bitmap };
			await UITestHelper.Load(image);

			// Measured between frames, without awaiting anything else, so the loop keeps resuming inside the notifications.
			var mostKept = 0;
			for (var i = 0; i < 20; i++)
			{
				await bitmap.SetSourceAsync(new MemoryStream(i % 2 == 0 ? square : tall));
				mostKept = Math.Max(mostKept, decoder.All.Count(d => !d.IsReleased));
			}

			await WindowHelper.WaitFor(() => bitmap.IsOpened, 10000, "Pre-condition: the last frame is decoded");
			await WindowHelper.WaitForIdle();

			Assert.IsTrue(mostKept <= 4, $"Only the frames being shown and replaced may be kept, not {mostKept}");
			Assert.IsTrue(decoder.All.Count(d => !d.IsReleased) <= 2, "Once the frames stop, only the last one is kept");
		}
		finally
		{
			WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25114")]
	public async Task When_Source_Changed_While_Displayed_Then_Previous_Pixels_Kept_Until_New_Shown()
	{
		// A source that changes while displayed keeps showing its previous pixels until the new ones are decoded, as on
		// WinUI, so a live source (camera frames, a refreshed preview) does not flicker.
		var square = await ReadAssetAsync(SquareAssetUri);
		var tall = await ReadAssetAsync(TallAssetUri);

		try
		{
			var bitmap = new BitmapImage();
			var image = new Image { Width = 100, Height = 40, Source = bitmap };
			await UITestHelper.Load(image);

			var opened = TrackOpened(image);
			await bitmap.SetSourceAsync(new MemoryStream(square));
			await opened.WaitAsync(TimeSpan.FromSeconds(10));
			var first = GetSurface(bitmap);

			// SetSourceAsync completes on the data the source already has, so wait for the Image to open the new stream.
			opened = TrackOpened(image);
			var second = bitmap.SetSourceAsync(new MemoryStream(tall));
			Assert.IsNotNull(first.Image, "The Image keeps showing the previous pixels while the new ones are decoded");

			await second.WaitAsync(TimeSpan.FromSeconds(10));
			await opened.WaitAsync(TimeSpan.FromSeconds(10));
			await WindowHelper.WaitForIdle();

			Assert.AreEqual(150, GetSurface(bitmap)?.Image?.PixelHeight);
			Assert.IsNull(first.Image, "The previous pixels are released once the Image shows the new ones");
		}
		finally
		{
			WindowHelper.WindowContent = null;
		}
	}

	[TestMethod]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25114")]
	public void When_Listener_Unsubscribes_Another_During_Notification_Then_It_Is_Not_Called()
	{
		// WriteableBitmap opens synchronously, so its subscribers are notified inside Invalidate.
		var bitmap = new WriteableBitmap(4, 4);
		var armed = false;
		IDisposable second = null;
		var secondCalls = 0;

		using var first = bitmap.Subscribe(_ =>
		{
			if (armed)
			{
				armed = false;
				second.Dispose();
			}
		});
		second = bitmap.Subscribe(_ => secondCalls++);
		secondCalls = 0;

		armed = true;
		bitmap.Invalidate();

		Assert.AreEqual(0, secondCalls, "A listener unsubscribed by an earlier one during the notification must not be called");
	}

	[TestMethod]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25114")]
	public void When_Listener_Changes_Source_During_Notification_Then_Stale_Data_Not_Delivered()
	{
		var bitmap = new WriteableBitmap(4, 4);
		var armed = false;
		var secondCalls = 0;

		// The first listener changes the source while it is notified: the new data reaches every listener, and the data it
		// replaced (released by the change) must not be delivered after it.
		using var first = bitmap.Subscribe(_ =>
		{
			if (armed)
			{
				armed = false;
				bitmap.Invalidate();
			}
		});
		using var second = bitmap.Subscribe(_ => secondCalls++);
		secondCalls = 0;

		armed = true;
		bitmap.Invalidate();

		Assert.AreEqual(1, secondCalls, "The second listener must get the new data once, and not the replaced data after it");
	}

	// A limit of 0 releases what nothing displays after the layout pass.
	private static IDisposable ReleaseAfterLayoutPass() => SetMaxUndisplayedImageBytes(0);

	private static IDisposable SetMaxUndisplayedImageBytes(long bytes)
	{
		var previous = FeatureConfiguration.Image.MaxUndisplayedImageBytes;
		FeatureConfiguration.Image.MaxUndisplayedImageBytes = bytes;
		return Disposable.Create(() => FeatureConfiguration.Image.MaxUndisplayedImageBytes = previous);
	}

	private static IDisposable SetReleaseDelay(TimeSpan delay)
	{
		var previous = PendingImageReleases.ReleaseDelay;
		PendingImageReleases.ReleaseDelay = delay;
		return Disposable.Create(() => PendingImageReleases.ReleaseDelay = previous);
	}

	private static IDisposable DisableBitmapImageCache() => SetBitmapImageCache(false);

	private static IDisposable EnableBitmapImageCache() => SetBitmapImageCache(true);

	private static IDisposable SetBitmapImageCache(bool enabled)
	{
		var wasEnabled = FeatureConfiguration.Image.EnableBitmapImageCache;
		FeatureConfiguration.Image.EnableBitmapImageCache = enabled;
		return Disposable.Create(() => FeatureConfiguration.Image.EnableBitmapImageCache = wasEnabled);
	}

	// The surface the source delivers to its subscribers; call only while something displays the source, as the probe's
	// own subscription would otherwise open it.
	private static CompositionImageSurface GetSurface(ImageSource source)
	{
		CompositionImageSurface surface = null;
		source.Subscribe(data => surface = data.CompositionSurface as CompositionImageSurface).Dispose();
		return surface;
	}

	private static async Task LoadAndWaitForOpened(Image image)
	{
		var opened = TrackOpened(image);
		await UITestHelper.Load(image);
		await opened.WaitAsync(TimeSpan.FromSeconds(10));
	}

	private static async Task AddAndWaitForOpened(Panel panel, Image image)
	{
		var opened = TrackOpened(image);
		panel.Children.Add(image);
		await opened.WaitAsync(TimeSpan.FromSeconds(10));
	}

	private static async Task<byte[]> ReadAssetAsync(string uri)
	{
		var file = await StorageFile.GetFileFromApplicationUriAsync(new Uri(uri));
		using var stream = await file.OpenStreamForReadAsync();
		using var memory = new MemoryStream();
		await stream.CopyToAsync(memory);
		return memory.ToArray();
	}

	private static Task TrackOpened(Image image)
	{
		var tcs = new TaskCompletionSource();
		image.ImageOpened += (_, _) => tcs.TrySetResult();
		image.ImageFailed += (_, e) => tcs.TrySetException(new Exception($"Failed to load the image: {e.ErrorMessage}"));
		return tcs.Task;
	}

	private static Task<bool> TrackOpen(BitmapImage image)
	{
		var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		image.ImageOpened += (_, _) => tcs.TrySetResult(true);
		image.ImageFailed += (_, _) => tcs.TrySetResult(false);
		return tcs.Task;
	}

	/// <summary>
	/// Wraps the registered image codec for the duration of a test and records every image it produces, so a test can
	/// tell whether the pixels of one particular decode were released.
	/// </summary>
	private sealed class TrackingDecoder : IImageEncoderDecoder, IDisposable
	{
		private readonly IImageEncoderDecoder _inner;
		private readonly ConcurrentQueue<TrackedImage> _images = new();

		private TrackingDecoder(IImageEncoderDecoder inner) => _inner = inner;

		public static TrackingDecoder Install()
		{
			var decoder = new TrackingDecoder(ImageEncoderDecoder.Current);
			ImageEncoderDecoder.Current = decoder;
			return decoder;
		}

		public void Dispose() => ImageEncoderDecoder.Current = _inner;

		public TrackedImage[] Decoded(int pixelWidth, int pixelHeight)
			=> _images.Where(i => i.PixelWidth == pixelWidth && i.PixelHeight == pixelHeight).ToArray();

		public int Count(int pixelWidth, int pixelHeight) => Decoded(pixelWidth, pixelHeight).Length;

		public TrackedImage[] All => _images.ToArray();

		public bool TryDecode(Stream stream, int? targetWidth, int? targetHeight, [NotNullWhen(true)] out ImageFrames frames)
		{
			if (!_inner.TryDecode(stream, targetWidth, targetHeight, out var decoded))
			{
				frames = null;
				return false;
			}

			frames = new ImageFrames(decoded.Frames.Select(Track).ToArray(), decoded.DurationsMs);
			return true;
		}

		public IImage CreateImage(int pixelWidth, int pixelHeight, ReadOnlySpan<byte> bgraPremul)
			=> Track(_inner.CreateImage(pixelWidth, pixelHeight, bgraPremul));

		public ImageFrames CreateFrames(IImage image) => _inner.CreateFrames(image);

		public void Encode(Stream destination, byte[] pixels, int width, int height, BitmapPixelFormat pixelFormat, BitmapAlphaMode alphaMode, BitmapEncoderFormat format, int quality)
			=> _inner.Encode(destination, pixels, width, height, pixelFormat, alphaMode, format, quality);

		private IImage Track(IImage image)
		{
			var tracked = new TrackedImage(image);
			_images.Enqueue(tracked);
			return tracked;
		}
	}

	private sealed class MeasureHook : FrameworkElement
	{
		public Action Measuring { get; set; }

		protected override Size MeasureOverride(Size availableSize)
		{
			Measuring?.Invoke();
			return default;
		}
	}

	private sealed class TrackedImage : DrawingResource, IImage
	{
		private readonly IImage _inner;
		private volatile bool _isReleased;

		public TrackedImage(IImage inner)
		{
			_inner = inner;

			// Read once: the inner image can't be queried once it is released.
			PixelWidth = inner.PixelWidth;
			PixelHeight = inner.PixelHeight;
		}

		public bool IsReleased => _isReleased;

		public int PixelWidth { get; }

		public int PixelHeight { get; }

		public void CopyPixels(Span<byte> destination) => _inner.CopyPixels(destination);

		protected override void Free()
		{
			_inner.Dispose();
			_isReleased = true;
		}
	}

	/// <summary>
	/// Minimal loopback HTTP server that sends the response headers immediately but holds the image bytes
	/// until <see cref="ReleaseResponses"/>, so a test can act while a download is deterministically in flight.
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
			var body = await ReadAssetAsync(SquareAssetUri);
			var listener = new TcpListener(IPAddress.Loopback, 0);
			listener.Start();
			return new GatedImageServer(listener, body);
		}

		public Uri Uri { get; }

		public int RequestCount => Volatile.Read(ref _requestCount);

		public int BodiesSent => Volatile.Read(ref _bodiesSent);

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
