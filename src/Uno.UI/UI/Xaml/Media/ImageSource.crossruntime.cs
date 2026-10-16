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
		// In subscription order, which is the notification order; a linked list, so unsubscribing is O(1).
		private readonly LinkedList<Subscription> _subscriptions = new();

		// Whether the async open in _opening has yet to deliver: a subscriber arriving meanwhile waits for it.
		private bool _isOpening;

		// Owned data the source replaced while a subscriber still drew it or was given it; released once none does.
		private List<ImageData> _replacedImageData;

		// Replaced data no subscriber draws or was given, waiting to be released (see PendingImageReleases) unless one draws
		// it again.
		private List<(ImageData Data, PendingImageReleases.Entry Entry)> _unshownReplacedImageData;

		// Owned surfaces an Image drew when it left. Once no subscriber draws them, they wait like data nothing subscribes
		// to rather than going after the pass, as that Image may come back with them.
		private List<ICompositionSurface> _leftWithSurfaces;

		// How many times the subscriptions' Shown and Pending refer to each surface, so whether one holds it is O(1).
		private Dictionary<ICompositionSurface, int> _holdCounts;

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
		/// <param name="shown">
		/// What the subscriber still draws from an earlier subscription to this source (an Image back in the tree). While the
		/// source has data or is opening, that is kept until the subscriber draws what replaced it; once the source failed
		/// or was cleared, the subscriber is given an empty image instead, so it stops drawing it.
		/// </param>
		/// <param name="appliedOnDelivery">
		/// Whether the subscriber draws what it is given at once (a brush). One that draws it later (an Image, when measured)
		/// calls <see cref="Subscription.Applied"/> then, and what it drew until then stays meanwhile.
		/// </param>
		internal Subscription Subscribe(Action<ImageData> onSourceOpened, ICompositionSurface shown = null, bool appliedOnDelivery = true)
		{
			var subscription = new Subscription(this, onSourceOpened, appliedOnDelivery, KeepReplaced(shown));
			subscription.Node = _subscriptions.AddLast(subscription);
			CancelPendingRelease();

			if (_imageData.HasData)
			{
				subscription.Deliver(_imageData);
				ReleaseReplacedNoLongerShown(TimeSpan.Zero);
			}
			else if (_subscriptions.Count == 1 && !_isOpening)
			{
				Open();
			}
			else if (shown is not null && !_isOpening)
			{
				// The source failed or was cleared while the subscriber was away: it must stop drawing what it had, which the
				// cache may keep alive. It gets an empty image, not the error: as on WinUI, attaching to a source that already
				// failed raises nothing.
				subscription.Deliver(ImageData.Empty);
			}

			return subscription;
		}

		/// <summary>
		/// A subscriber to this source, and what it draws and was given, which the source does not release.
		/// </summary>
		internal sealed class Subscription : IDisposable
		{
			private readonly ImageSource _source;
			private readonly Action<ImageData> _callback;
			private bool _isDisposed;

			internal Subscription(ImageSource source, Action<ImageData> callback, bool appliedOnDelivery, ImageData shown)
			{
				_source = source;
				_callback = callback;
				AppliedOnDelivery = appliedOnDelivery;
				Set(shown, default);
			}

			/// <summary>
			/// Whether the subscriber draws what it is given at once (see <see cref="Subscribe"/>).
			/// </summary>
			internal bool AppliedOnDelivery { get; }

			/// <summary>
			/// What the subscriber draws: what it last applied, or still draws from an earlier subscription.
			/// </summary>
			internal ImageData Shown { get; private set; }

			/// <summary>
			/// What the subscriber was given and has yet to draw.
			/// </summary>
			internal ImageData Pending { get; private set; }

			/// <summary>
			/// Whether the subscription was not disposed, which also takes it out of the source's subscriptions.
			/// </summary>
			internal bool IsActive => !_isDisposed;

			internal LinkedListNode<Subscription> Node { get; set; }

			// Keeps the source's hold counts in step with Shown and Pending.
			private void Set(ImageData shown, ImageData pending)
			{
				if (!_isDisposed)
				{
					_source.CountHold(shown, 1);
					_source.CountHold(pending, 1);
					_source.CountHold(Shown, -1);
					_source.CountHold(Pending, -1);
				}

				Shown = shown;
				Pending = pending;
			}

			internal void Deliver(ImageData data)
			{
				// Before the callback, which may change the source and so be given newer data, or throw.
				if (AppliedOnDelivery)
				{
					Set(data, default);
				}
				else
				{
					Set(Shown, data);
				}

				_callback(data);
			}

			/// <summary>
			/// Tells the source that the subscriber now draws <paramref name="data"/>, the data it was last given (or an empty
			/// image once it draws nothing), so what it drew before can be released.
			/// </summary>
			internal void Applied(ImageData data)
			{
				if (!_isDisposed)
				{
					Set(data, default);
					_source.OnApplied();
				}
			}

			public void Dispose()
			{
				if (!_isDisposed)
				{
					_isDisposed = true;
					_source.Unsubscribe(this);
				}
			}
		}

		private void Unsubscribe(Subscription subscription)
		{
			_subscriptions.Remove(subscription.Node);

			// An Image keeps drawing what it drew off the tree (its sprite), and may come back with it.
			var surface = subscription.Shown.CompositionSurface;
			if (!subscription.AppliedOnDelivery && IsReleasable(subscription.Shown) && _leftWithSurfaces?.Contains(surface) != true)
			{
				(_leftWithSurfaces ??= new()).Add(surface);
			}

			CountHold(subscription.Shown, -1);
			CountHold(subscription.Pending, -1);
			ReleaseReplacedNoLongerShown(delay: null);

			if (_subscriptions.Count == 0)
			{
				ScheduleReleaseUnused();
			}
		}

		private void OnApplied() => ReleaseReplacedNoLongerShown(TimeSpan.Zero);

		private void CountHold(ImageData data, int change)
		{
			if (data.CompositionSurface is { } surface)
			{
				var counts = _holdCounts ??= new(ReferenceEqualityComparer.Instance);
				var count = counts.GetValueOrDefault(surface) + change;
				if (count > 0)
				{
					counts[surface] = count;
				}
				else
				{
					counts.Remove(surface);
				}
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
				&& (IsReleasable(_imageData) || _isOpening))
			{
				_pendingRelease = PendingImageReleases.Add(GetByteCount(_imageData), ReleaseUnused);
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

			if (_unshownReplacedImageData is { } unshown)
			{
				_unshownReplacedImageData = null;
				foreach (var (data, entry) in unshown)
				{
					PendingImageReleases.Remove(entry);
					ReleaseSurface(data);
				}
			}

			_leftWithSurfaces = null;
		}

		/// <summary>
		/// Hands the replaced data no subscriber draws or was given to <see cref="PendingImageReleases"/>, where one that
		/// draws it again takes it back. What a subscriber stopped drawing, as it drew what replaced it, is released after
		/// the layout pass (a <paramref name="delay"/> of zero); what a subscriber that drew it took away when it left waits
		/// like data nothing subscribes to, so that subscriber can come back with it (see <see cref="Subscribe"/>).
		/// </summary>
		private void ReleaseReplacedNoLongerShown(TimeSpan? delay)
		{
			if (_replacedImageData is not { } replaced)
			{
				return;
			}

			List<(ImageData Data, TimeSpan? Delay)> unshown = null;
			for (var i = replaced.Count - 1; i >= 0; i--)
			{
				var surface = replaced[i].CompositionSurface;

				// A WriteableBitmap draws its new pixels into the same surface.
				var isCurrent = ReferenceEquals(surface, _imageData.CompositionSurface);
				if (!isCurrent && _holdCounts?.ContainsKey(surface) == true)
				{
					continue;
				}

				if (!isCurrent)
				{
					var wait = _leftWithSurfaces?.Remove(surface) == true ? null : delay;
					(unshown ??= new()).Add((replaced[i], wait));
				}

				replaced.RemoveAt(i);
			}

			if (replaced.Count == 0)
			{
				_replacedImageData = null;
			}

			if (unshown is null)
			{
				return;
			}

			_unshownReplacedImageData ??= new();
			foreach (var (data, wait) in unshown)
			{
				PendingImageReleases.Entry entry = null;
				// Data a subscriber stopped drawing goes after the pass anyway: counting it would have older waiting images go too.
				entry = PendingImageReleases.Add(
					wait == TimeSpan.Zero ? 0 : GetByteCount(data),
					() =>
					{
						_unshownReplacedImageData?.RemoveAll(u => u.Entry == entry);
						ReleaseSurface(data);
					},
					wait);
				_unshownReplacedImageData.Add((data, entry));
			}
		}

		/// <summary>
		/// What a subscriber back with <paramref name="surface"/> draws: the current data, or replaced data, taken back from its
		/// wait if no subscriber drew it anymore, as long as the subscriber will be given what replaced it.
		/// </summary>
		private ImageData KeepReplaced(ICompositionSurface surface)
		{
			// A source that failed or was cleared gives nothing more: the subscriber must show nothing, not keep the old image.
			// Without subscribers, Subscribe opens the source.
			if (surface is null || !(_isOpening || _imageData.HasData || _subscriptions.Count == 0))
			{
				return default;
			}

			_leftWithSurfaces?.Remove(surface);
			if (ReferenceEquals(surface, _imageData.CompositionSurface))
			{
				return _imageData;
			}

			var index = _replacedImageData?.FindIndex(d => ReferenceEquals(d.CompositionSurface, surface)) ?? -1;
			if (index >= 0)
			{
				return _replacedImageData[index];
			}

			index = _unshownReplacedImageData?.FindIndex(u => ReferenceEquals(u.Data.CompositionSurface, surface)) ?? -1;
			if (index < 0)
			{
				return default;
			}

			var (data, entry) = _unshownReplacedImageData[index];
			_unshownReplacedImageData.RemoveAt(index);
			PendingImageReleases.Remove(entry);
			(_replacedImageData ??= new()).Add(data);
			return data;
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

		/// <summary>
		/// Opening runs app code (a completed cache entry raises ImageOpened before TryOpenSourceAsync returns), which may
		/// supersede the open, cancelling <c>ct</c>, or dispose the source: only an open still current updates its state.
		/// </summary>
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
					if (!ct.IsCancellationRequested)
					{
						OnOpened(img);
					}
					else if (!ReferenceEquals(img.CompositionSurface, _imageData.CompositionSurface))
					{
						// A WriteableBitmap opens into the one surface it has, which the open that superseded this one shows.
						ReleaseSurface(img);
					}
				}
				else if (TryOpenSourceAsync(ct, null, null, out var asyncImg))
				{
					// The result of a superseded open is still observed, to release it.
					var isEnqueued = DispatcherQueue.TryEnqueue(async () =>
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

					if (!ct.IsCancellationRequested)
					{
						_isOpening = isEnqueued;
					}
				}
				else if (!ct.IsCancellationRequested)
				{
					OnOpened(new ImageData()); // Empty
				}
			}
			catch (Exception error)
			{
				if (!ct.IsCancellationRequested)
				{
					_isOpening = false;
					OnOpened(ImageData.FromError(error));
				}
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

				if (subscription.IsActive)
				{
					subscription.Deliver(data);
				}
			}

			ReleaseReplacedNoLongerShown(TimeSpan.Zero);

			// A decode that completed while nothing subscribes waits in place of the open.
			if (_pendingRelease is { } pending)
			{
				PendingImageReleases.Resize(pending, GetByteCount(_imageData));
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
