#if HAS_UNO
using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Private.Infrastructure;
using Uno.UI.RuntimeTests.Helpers;
using static Private.Infrastructure.TestServices;

namespace Uno.UI.RuntimeTests.Tests.Windows_UI_Xaml_Controls;

[TestClass]
[RunsOnUIThread]
[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.Skia)]
public class Given_Image_Release
{
	private const string AssetUri = "ms-appx:///Assets/my500x200.jpg";

	[TestMethod]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25114")]
	public async Task When_Unloaded_Then_Decoded_Image_Released()
	{
		// The source owns its decoded data only when the cache does not share it between sources.
		var cacheWasEnabled = FeatureConfiguration.Image.EnableBitmapImageCache;
		FeatureConfiguration.Image.EnableBitmapImageCache = false;

		try
		{
			var bitmap = new BitmapImage(new Uri(AssetUri));
			var image = new Image { Width = 100, Height = 40, Source = bitmap };

			await LoadAndWaitForOpened(image);
			Assert.IsTrue(bitmap.IsOpened, "Pre-condition: the bitmap is decoded while an Image shows it");

			WindowHelper.WindowContent = null;
			await WindowHelper.WaitForIdle();
			Assert.IsFalse(bitmap.IsOpened, "Nothing displays the bitmap once its only Image is unloaded, so the decoded data must be released");

			// Showing it again decodes it again.
			await LoadAndWaitForOpened(image);
			Assert.IsTrue(bitmap.IsOpened, "An Image that is loaded again must decode its source again");
		}
		finally
		{
			WindowHelper.WindowContent = null;
			FeatureConfiguration.Image.EnableBitmapImageCache = cacheWasEnabled;
		}
	}

	[TestMethod]
	[GitHubWorkItem("https://github.com/unoplatform/uno/issues/25114")]
	public async Task When_Source_Replaced_Then_Previous_Decoded_Image_Released()
	{
		var cacheWasEnabled = FeatureConfiguration.Image.EnableBitmapImageCache;
		FeatureConfiguration.Image.EnableBitmapImageCache = false;

		try
		{
			var first = new BitmapImage(new Uri(AssetUri));
			var image = new Image { Width = 100, Height = 40, Source = first };

			await LoadAndWaitForOpened(image);
			Assert.IsTrue(first.IsOpened, "Pre-condition: the first bitmap is decoded while the Image shows it");

			var opened = TrackOpened(image);
			image.Source = new BitmapImage(new Uri(AssetUri));
			await opened.WaitAsync(TimeSpan.FromSeconds(10));

			Assert.IsFalse(first.IsOpened, "The replaced bitmap is shown by nothing, so its decoded data must be released");
		}
		finally
		{
			WindowHelper.WindowContent = null;
			FeatureConfiguration.Image.EnableBitmapImageCache = cacheWasEnabled;
		}
	}

	private static async Task LoadAndWaitForOpened(Image image)
	{
		var opened = TrackOpened(image);
		await UITestHelper.Load(image);
		await opened.WaitAsync(TimeSpan.FromSeconds(10));
	}

	private static Task TrackOpened(Image image)
	{
		var tcs = new TaskCompletionSource();
		image.ImageOpened += (_, _) => tcs.TrySetResult();
		image.ImageFailed += (_, e) => tcs.TrySetException(new Exception($"Failed to load the image: {e.ErrorMessage}"));
		return tcs.Task;
	}
}
#endif
