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
		private readonly List<Subscription> _subscriptions = new List<Subscription>();

		// Whether the async open in _opening has yet to deliver: a subscriber arriving meanwhile waits for it.
		private bool _isOpening;

		// Owned data the source replaced while a subscriber still showed it; released once none does.
		private List<ImageData> _replacedImageData;

		// Changes whenever _imageData is replaced.
		private int _dataVersion;

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
			var subscription = new Subscription(onSourceOpened);
			_subscriptions.Add(subscription);
			CancelPendingRelease();

			if (_imageData.HasData)
			{
				subscription.Deliver(_imageData);
			}
			else if (_subscriptions.Count == 1 && !_isOpening)
			{
				Open();
			}

			return Disposable.Create(() =>
			{
				_subscriptions.Remove(subscription);

				if (_subscriptions.Count == 0)
				{
					ScheduleReleaseUnused();
				}
			});
		}

		private sealed class Subscription(Action<ImageData> callback)
		{
			// The data last delivered, which the subscriber may still be showing.
			public ImageData Shown { get; private set; }

			public void Deliver(ImageData data)
			{
				Shown = data;
				callback(data);
			}
		}

		/// <summary>
		/// Changes when what this source shows changes (a new UriSource or stream, an invalidation), but not when its
		/// decoded data is only released and decoded again: the opened events are raised once per version, as WinUI
		/// raises them once per source.
		/// </summary>
		internal int SourceVersion { get; private protected set; }

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
				&& (IsReleasable(_imageData) || _replacedImageData is { Count: > 0 } || _isOpening))
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

		private long GetOwnedByteCount()
		{
			var count = GetByteCount(_imageData);
			if (_replacedImageData is { } replaced)
			{
				foreach (var data in replaced)
				{
					count += GetByteCount(data);
				}
			}

			return count;
		}

		private static long GetByteCount(ImageData data)
			=> IsReleasable(data) ? ((CompositionImageSurface)data.CompositionSurface!).DecodedByteCount : 0;

		private void ReleaseUnused()
		{
			_pendingRelease = null;

			// An open nothing waits for is stopped, which cancels an uncached download.
			_opening.Disposable = null;
			_isOpening = false;
			ReleaseReplaced();

			if (IsReleasable(_imageData))
			{
				ReleaseSurface(_imageData);
				_imageData = ImageData.Empty;
				_dataVersion++;
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
				ReleaseReplaced();
				ReleaseSurface(_imageData);
			}
			else if (IsReleasable(_imageData))
			{
				// The subscribers keep showing it until they have what replaces it, as WinUI keeps the surface while it
				// switches URIs, to avoid flickering (MUX Reference: CImageSource::ReloadImage, imagesource.cpp).
				(_replacedImageData ??= new()).Add(_imageData);
			}

			_imageData = ImageData.Empty;
			_dataVersion++;
		}

		private void ReleaseReplaced()
		{
			if (_replacedImageData is { } replaced)
			{
				_replacedImageData = null;
				replaced.ForEach(ReleaseSurface);
			}
		}

		/// <summary>
		/// Releases, after the layout pass that applies what the subscribers were last given (an Image applies it when
		/// measured), the replaced data none of them still shows, so no frame is recorded with neither.
		/// </summary>
		private void ReleaseReplacedNoLongerShown()
		{
			if (_replacedImageData is not { } replaced)
			{
				return;
			}

			List<ImageData> unshown = null;
			for (var i = replaced.Count - 1; i >= 0; i--)
			{
				var surface = replaced[i].CompositionSurface;

				// A WriteableBitmap draws its new pixels into the same surface.
				var isCurrent = ReferenceEquals(surface, _imageData.CompositionSurface);
				if (!isCurrent && _subscriptions.Exists(s => ReferenceEquals(s.Shown.CompositionSurface, surface)))
				{
					continue;
				}

				if (!isCurrent)
				{
					(unshown ??= new()).Add(replaced[i]);
				}

				replaced.RemoveAt(i);
			}

			if (replaced.Count == 0)
			{
				_replacedImageData = null;
			}

			if (unshown is not null)
			{
				PendingImageReleases.Add(0, () => unshown.ForEach(ReleaseSurface), delay: TimeSpan.Zero);
			}
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
			SourceVersion++;

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
							// A superseded open that fails publishes nothing: the source has moved on.
							if (!ct.IsCancellationRequested)
							{
								_isOpening = false;
								OnOpened(ImageData.FromError(error));
							}
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
			var version = ++_dataVersion;

			if (this.Log().IsEnabled(LogLevel.Debug))
			{
				this.Log().Debug($"Image {this} opened with {data}");
			}

			// A listener can unsubscribe others, or change the source and so release this data: only listeners still
			// subscribed get it, and only while it is still the source's. The ones a change skipped keep what they had.
			foreach (var subscription in _subscriptions.ToArray())
			{
				if (_dataVersion != version)
				{
					break;
				}

				if (_subscriptions.Contains(subscription))
				{
					subscription.Deliver(data);
				}
			}

			ReleaseReplacedNoLongerShown();

			// A decode that completed while nothing subscribes waits with what this source had.
			if (_pendingRelease is { } pending)
			{
				PendingImageReleases.Resize(pending, GetOwnedByteCount());
			}
		}

		/// <summary>
		/// Opens the source again for the subscribers it still has, after Dispose dropped its data.
		/// </summary>
		private void ReopenForSubscribers()
		{
			if (_subscriptions.Count > 0)
			{
				Open();
			}
		}
	}
}
