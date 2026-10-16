using Microsoft.UI.Composition;
using Uno.Disposables;
using Uno.UI.Xaml.Media;

namespace Microsoft.UI.Xaml.Media
{
	public partial class ImageBrush
	{
		private readonly SerialDisposable _sourceSubscription = new SerialDisposable();

		// Changes on every ImageSource change, so a Subscribe that the app superseded meanwhile is noticed.
		private int _sourceVersion;

		internal ImageData? ImageDataCache;

		partial void OnSourceChangedPartial(ImageSource newValue, ImageSource oldValue)
		{
			// The new source draws nothing until it opens, as on WinUI, where a brush draws its source's surface. The
			// composition brush lets go of the old image before the subscription can release it.
			ImageDataCache = default;
			if (_compositionBrush is CompositionSurfaceBrush surfaceBrush)
			{
				surfaceBrush.Surface = null;
			}

			// A UriSource change needs no new subscription: the source opens the new URI for this one, and the brush draws what
			// it has until then, as an Image does (and as WinUI keeps the surface while it switches URIs).
			var version = ++_sourceVersion;
			var subscription = newValue?.Subscribe(OnSourceOpened);

			// Subscribing ran app code (an ImageOpened handler) that may have set another ImageSource, subscribed to by now.
			if (version == _sourceVersion)
			{
				_sourceSubscription.Disposable = subscription;
			}
			else
			{
				subscription?.Dispose();
			}
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
