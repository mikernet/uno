#nullable enable

using Uno.UI.Xaml.Media;

namespace Microsoft.UI.Xaml.Media;

partial class ImageSource
{
	partial void ReleaseImageDataPlatform()
	{
		if (_imageData.Kind == ImageDataKind.CompositionSurface)
		{
			_imageData.CompositionSurface?.ReleaseFrames();
		}
	}
}
