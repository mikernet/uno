using Uno.Extensions;
using Uno.Foundation.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Windows.UI.Core;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Controls.Primitives;
using Uno;
using Uno.Diagnostics.Eventing;
using Microsoft.UI.Xaml.Media.Imaging;
using Uno.Disposables;
using Windows.Devices.Enumeration;
using Uno.UI.Xaml.Media;


#if !IS_UNO
using Uno.Web.Query;
using Uno.Web.Query.Cache;
#endif

namespace Microsoft.UI.Xaml.Media
{
	partial class ImageSource
	{
		private readonly SerialDisposable _opening = new SerialDisposable();
		private readonly List<Action<ImageData>> _subscriptions = new List<Action<ImageData>>();

		// Whether the async open in _opening has yet to deliver: a subscriber arriving meanwhile waits for it.
		private bool _isOpening;

		// What the subscribers still show while the source opens what replaced it.
		private ImageData _outgoingImageData;

		// The wait of what this source decoded while nothing subscribes to it.
		private PendingImageReleases.Entry _pendingRelease;

		protected ImageSource()
		{

		}

		partial void InitFromResource(Uri uri)
		{
			// TODO: Unify
#if __SKIA__
			AbsoluteUri = uri;
#else
			var path = uri.PathAndQuery.TrimStart("/");
			AbsoluteUri = new Uri(path, UriKind.Relative);
#endif
		}

		partial void CleanupResource()
		{
			AbsoluteUri = null;
		}

		/// <summary>
		/// Subscribes to this image source
		/// </summary>
		/// <param name="onSourceOpened">
		/// A callback that will be invoked each time the source has been updated
		/// (i.e. a property has changed on the source AND the source has been re-opened)
		/// </param>
		internal IDisposable Subscribe(Action<ImageData> onSourceOpened)
		{
			_subscriptions.Add(onSourceOpened);
			CancelPendingRelease();

			if (_imageData.HasData)
			{
				onSourceOpened(_imageData);
			}
			else if (_subscriptions.Count == 1 && !_isOpening)
			{
				Open();
			}

			return Disposable.Create(() =>
			{
				_subscriptions.Remove(onSourceOpened);

				if (_subscriptions.Count == 0)
				{
					ScheduleReleaseUnused();
				}
			});
		}

		/// <summary>
		/// Number of decoded surfaces released ahead of finalization; for tests.
		/// </summary>
		internal static int ReleasedSurfacesForTesting;

		/// <summary>
		/// Releases what this source decoded for its subscribers once it has waited for one to come back (see
		/// <see cref="PendingImageReleases"/>), so an element that leaves the tree and enters it again meanwhile
		/// (re-parenting, re-templating, container recycling) keeps its pixels.
		/// </summary>
		private void ScheduleReleaseUnused()
		{
			// An SvgImageSource opens without subscribers and keeps its parsed vector document until its source changes.
			// Data shared through the bitmap cache stays: the source never releases it.
			if (this is not SvgImageSource
				&& _pendingRelease is null
				&& (IsReleasable(_imageData) || IsReleasable(_outgoingImageData) || _isOpening))
			{
				_pendingRelease = PendingImageReleases.Add(GetOwnedByteCount(), ReleaseUnused);
			}
		}

		private void CancelPendingRelease()
		{
			if (_pendingRelease is { } pending)
			{
				PendingImageReleases.Remove(pending);
				_pendingRelease = null;
			}
		}

		private long GetOwnedByteCount() => GetByteCount(_imageData) + GetByteCount(_outgoingImageData);

		private static long GetByteCount(ImageData data)
			=> IsReleasable(data) ? ((CompositionImageSurface)data.CompositionSurface!).DecodedByteCount : 0;

		private void ReleaseUnused()
		{
			_pendingRelease = null;

			// An open nothing waits for is stopped, which cancels an uncached download.
			_opening.Disposable = null;
			_isOpening = false;
			ReleaseOutgoing();

			if (IsReleasable(_imageData))
			{
				ReleaseSurface(_imageData);
				_imageData = ImageData.Empty;
			}
		}

		/// <summary>
		/// Cancels an open in flight and drops the decoded data, for a change of the source. Data this source owns is
		/// released at once when nothing shows it, and otherwise once the subscribers have what replaces it.
		/// </summary>
		private void ReleaseImageData()
		{
			_opening.Disposable = null;
			_isOpening = false;

			if (_subscriptions.Count == 0)
			{
				CancelPendingRelease();
				ReleaseOutgoing();
				ReleaseSurface(_imageData);
			}
			else if (IsReleasable(_imageData))
			{
				// WinUI also keeps the surface while it switches URIs, to avoid flickering (MUX Reference:
				// CImageSource::ReloadImage, imagesource.cpp). Data still being opened leaves the outgoing one in place.
				ReleaseOutgoing();
				_outgoingImageData = _imageData;
			}

			_imageData = ImageData.Empty;
		}

		private void ReleaseOutgoing()
		{
			ReleaseSurface(_outgoingImageData);
			_outgoingImageData = ImageData.Empty;
		}

		private static bool IsReleasable(ImageData data) => !data.IsShared && data.CompositionSurface is CompositionImageSurface;

		/// <summary>
		/// Releases the decoded frames of an image surface that nothing will display, unless the data is shared through
		/// the bitmap cache, in which case it is the cache's to release.
		/// </summary>
		private protected static void ReleaseSurface(ImageData data)
		{
			if (IsReleasable(data))
			{
				((CompositionImageSurface)data.CompositionSurface!).ReleaseFrames();
				Interlocked.Increment(ref ReleasedSurfacesForTesting);
			}
		}

		/// <summary>
		/// Indicates that this source has already been opened
		/// (So the onSourceOpened callback of Subscribe will be invoked synchronously!)
		/// </summary>
		internal bool IsOpened => _imageData.HasData;

		private protected void InvalidateSource()
		{
			ReleaseImageData();
			if (_subscriptions.Count > 0 || this is SvgImageSource)
			{
				Open();
			}
		}

		private void Open()
		{
			var cts = new CancellationTokenSource();
			var ct = cts.Token;
			_opening.Disposable = Disposable.Create(cts.Cancel);
			_isOpening = false;
			try
			{
				if (TryOpenSourceSync(null, null, out var img))
				{
					OnOpened(img);
				}
				else if (TryOpenSourceAsync(ct, null, null, out var asyncImg))
				{
					_isOpening = DispatcherQueue.TryEnqueue(async () =>
					{
						try
						{
							var data = await asyncImg;
							if (!ct.IsCancellationRequested)
							{
								_isOpening = false;
								OnOpened(data);
							}
							else
							{
								// A superseded open still produced an image nobody will show: release it now rather than leave
								// it to finalization, which is what a burst of source changes would otherwise pile up.
								ReleaseSurface(data);
							}
						}
						catch (OperationCanceledException) when (ct.IsCancellationRequested)
						{
						}
						catch (Exception error)
						{
							if (!ct.IsCancellationRequested)
							{
								_isOpening = false;
							}

							OnOpened(ImageData.FromError(error));
						}
					});
				}
				else
				{
					OnOpened(new ImageData()); // Empty
				}
			}
			catch (Exception error)
			{
				_isOpening = false;
				OnOpened(ImageData.FromError(error));
			}
		}

		private void OnOpened(ImageData data)
		{
			_imageData = data; // We should also cache the targetWidth and targetHeight

			// What the subscribers showed until now goes once they show this.
			var outgoing = _outgoingImageData;
			_outgoingImageData = ImageData.Empty;

			if (this.Log().IsEnabled(LogLevel.Debug))
			{
				this.Log().Debug($"Image {this} opened with {data}");
			}

			var listeners = _subscriptions.ToList();
			foreach (var listener in listeners)
			{
				listener(data);
			}

			// A WriteableBitmap draws its new pixels into the same surface. Others go after the layout pass that applies
			// the new data (an Image applies it when measured), so no frame is recorded with neither.
			if (IsReleasable(outgoing) && !ReferenceEquals(outgoing.CompositionSurface, data.CompositionSurface))
			{
				PendingImageReleases.Add(0, () => ReleaseSurface(outgoing), delay: TimeSpan.Zero);
			}

			// A decode that completed while nothing subscribes waits with what this source had.
			if (_pendingRelease is { } pending)
			{
				PendingImageReleases.Resize(pending, GetOwnedByteCount());
			}
		}
	}
}
