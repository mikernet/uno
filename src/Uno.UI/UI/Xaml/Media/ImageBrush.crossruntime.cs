using Uno.Disposables;
using Uno.UI.Xaml.Media;

namespace Microsoft.UI.Xaml.Media
{
	public partial class ImageBrush
	{
		private readonly SerialDisposable _sourceSubscription = new SerialDisposable();

		internal ImageData? ImageDataCache;

		partial void OnSourceChangedPartial(ImageSource newValue, ImageSource oldValue)
		{
			ImageDataCache = default;

			// A UriSource change needs no new subscription: the source opens the new URI for this one, and the brush draws what
			// it has until then, as an Image does (and as WinUI keeps the surface while it switches URIs).
			_sourceSubscription.Disposable = newValue?.Subscribe(OnSourceOpened);
		}

		private void OnSourceOpened(ImageData image)
		{
			ImageDataCache = image;

			if (image.Kind == ImageDataKind.Error)
			{
				// Note: On WASM, images are loaded by the platform (we only propagate the url),
				//		 so the ImageFailed won't be raised if the url is invalid ...
				OnImageFailed();
			}
			else
			{
				OnImageOpened();
			}

			OnInvalidateRender();
		}
	}
}
