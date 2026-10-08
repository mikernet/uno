#nullable enable

using Uno.UI.Xaml.Media;

namespace Microsoft.UI.Xaml.Media;

partial class ImageSource
{
	/// <summary>
	/// Number of decoded surfaces released ahead of finalization; for tests.
	/// </summary>
	internal static int ReleasedSurfacesForTesting;

	partial void ReleaseImageDataPlatform() => ReleaseSurface(_imageData);

	partial void ReleaseAbandonedImageData(ImageData data)
	{
		if (!SharesImageData)
		{
			ReleaseSurface(data);
		}
	}

	/// <summary>
	/// Releases the decoded frames of an image surface that nothing will display.
	/// </summary>
	internal static void ReleaseSurface(ImageData data)
	{
		if (data.Kind == ImageDataKind.CompositionSurface && data.CompositionSurface is { } surface)
		{
			surface.ReleaseFrames();
			ReleasedSurfacesForTesting++;
		}
	}
}
