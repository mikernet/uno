#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Uno.UI;
using Uno.UI.Dispatching;
using Uno.UI.Xaml.Core;

namespace Microsoft.UI.Xaml.Media;

/// <summary>
/// Decoded images nothing displays, kept for <see cref="ReleaseDelay"/> in case they are displayed again, and released
/// sooner, oldest first, while together they hold more than <see cref="FeatureConfiguration.Image.MaxUndisplayedImageBytes"/>.
/// Releases run at the end of a layout pass, so an image re-parented or re-templated during a pass keeps its pixels.
/// UI thread only.
/// </summary>
internal static class PendingImageReleases
{
	/// <summary>
	/// WinUI frees the surfaces of an image that left the tree once it has been off it for
	/// sc_imageSurfaceWrapperReleaseDelayMilliseconds, because freeing them at once made images that come back take several
	/// frames to reload (MUX Reference: corep.h, CCoreServices::TrimMemoryOnSurfaceWrappers in xcpcore.cpp). Tests override
	/// it, as WinUI's do with m_testOverrideImageSurfaceWrapperReleaseDelay.
	/// </summary>
	internal static TimeSpan ReleaseDelay
	{
		get => _releaseDelay;
		set
		{
			_releaseDelay = value;

			// The waits under way end by the new delay, so when the first one ends is no longer known.
			_earliestDueAt = long.MinValue;
			StartTimer();
		}
	}

	private static TimeSpan _releaseDelay = TimeSpan.FromMilliseconds(1000);

	// In the order the images were let go, so the oldest is released first.
	private static readonly LinkedList<Entry> _entries = new();
	private static long _pendingBytes;

	// No wait ends before this timestamp. After a removal it may be earlier than the first end, which only costs a check
	// that finds nothing due; it spares a scan of the list on each addition and on each layout pass with nothing due.
	private static long _earliestDueAt = long.MaxValue;
	private static int _layoutPass;
	private static global::Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;
	private static long _timerDueAt;
	private static bool _isCheckQueued;

	internal sealed class Entry
	{
		internal Entry(long bytes, TimeSpan? delay, Action release)
		{
			Bytes = bytes;
			Delay = delay;
			Release = release;
			Since = Stopwatch.GetTimestamp();
			LayoutPass = _layoutPass;
		}

		internal long Bytes { get; set; }

		// Null waits ReleaseDelay.
		internal TimeSpan? Delay { get; }

		internal Action Release { get; }

		internal long Since { get; }

		internal int LayoutPass { get; }

		internal LinkedListNode<Entry>? Node { get; set; }

		internal bool IsPending => Node is not null;
	}

	/// <summary>
	/// Starts the wait of decoded data nothing displays. A <paramref name="delay"/> of zero releases it after the layout
	/// pass, e.g. data that was replaced, once the pass applies what replaced it.
	/// </summary>
	internal static Entry Add(long bytes, Action release, TimeSpan? delay = null)
	{
		var entry = new Entry(bytes, delay, release);
		entry.Node = _entries.AddLast(entry);
		_pendingBytes += bytes;
		_earliestDueAt = Math.Min(_earliestDueAt, GetDueAt(entry));
		OnChanged(entry);
		return entry;
	}

	/// <summary>
	/// Ends the wait without releasing, as the data is displayed again.
	/// </summary>
	internal static void Remove(Entry entry)
	{
		if (entry.Node is { } node)
		{
			_entries.Remove(node);
			entry.Node = null;
			_pendingBytes -= entry.Bytes;

			if (_entries.Count == 0)
			{
				_earliestDueAt = long.MaxValue;
			}
		}
	}

	/// <summary>
	/// Updates the size of waiting data, e.g. once a decode that was in flight completes.
	/// </summary>
	internal static void Resize(Entry entry, long bytes)
	{
		if (entry.IsPending)
		{
			_pendingBytes += bytes - entry.Bytes;
			entry.Bytes = bytes;
			OnChanged(entry);
		}
	}

	internal static void OnLayoutPassStarting() => _layoutPass++;

	internal static void OnLayoutPassCompleted()
	{
		if (_entries.Count == 0)
		{
			return;
		}

		if (_pendingBytes > FeatureConfiguration.Image.MaxUndisplayedImageBytes || Stopwatch.GetTimestamp() >= _earliestDueAt)
		{
			ReleaseDue(atLayoutPassEnd: true);
		}
		else
		{
			// Nothing is due yet, e.g. after a timer that fired a little early: the wait goes on.
			StartTimer();
		}
	}

	private static long GetDueAt(Entry entry)
		=> entry.Since + (long)((entry.Delay ?? ReleaseDelay).TotalSeconds * Stopwatch.Frequency);

	private static void OnChanged(Entry entry)
	{
		if (_pendingBytes > FeatureConfiguration.Image.MaxUndisplayedImageBytes || entry.Delay == TimeSpan.Zero)
		{
			RequestCheck();
		}
		else
		{
			StartTimer();
		}
	}

	private static void ReleaseDue(bool atLayoutPassEnd)
	{
		if (!atLayoutPassEnd)
		{
			_isCheckQueued = false;
		}

		var limit = FeatureConfiguration.Image.MaxUndisplayedImageBytes;
		var now = Stopwatch.GetTimestamp();
		var waitsForNextPass = false;

		_earliestDueAt = long.MaxValue;
		for (var node = _entries.First; node is not null;)
		{
			var entry = node.Value;
			var next = node.Next;

			// Releasing an open still in flight (no bytes yet) early would not bring the total under the limit.
			var dueAt = GetDueAt(entry);
			var isDue = (entry.Bytes > 0 && _pendingBytes > limit) || now >= dueAt;

			// At the end of a pass, only what was let go before it began: an element re-added during this pass may be
			// loaded by the next one, and data replaced during it is applied by the next one.
			if (isDue && atLayoutPassEnd && entry.LayoutPass == _layoutPass)
			{
				waitsForNextPass = true;
				isDue = false;
			}

			if (isDue)
			{
				Remove(entry);
				entry.Release();

				// A release can end other waits, the next one included; the walk then starts over.
				if (next is { List: null })
				{
					next = _entries.First;
					_earliestDueAt = long.MaxValue;
				}
			}
			else
			{
				_earliestDueAt = Math.Min(_earliestDueAt, dueAt);
			}

			node = next;
		}

		if (waitsForNextPass)
		{
			RequestCheck();
		}
		else
		{
			StartTimer();
		}
	}

	// Requests a check when the next wait ends, so it ends on an idle UI too.
	private static void StartTimer()
	{
		if (_entries.Count == 0)
		{
			return;
		}

		var now = Stopwatch.GetTimestamp();
		var dueAt = Math.Max(_earliestDueAt, now);
		if (_timer is { IsRunning: true } && _timerDueAt <= dueAt)
		{
			return;
		}

		var dueIn = TimeSpan.FromSeconds((dueAt - now) / (double)Stopwatch.Frequency);

		if (_timer is null)
		{
			_timer = global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
			_timer.IsRepeating = false;
			_timer.Tick += static (_, _) =>
			{
				if (_entries.Count > 0)
				{
					RequestCheck();
				}
			};
		}

		_timerDueAt = dueAt;
		_timer.Interval = dueIn;
		_timer.Start();
	}

	private static void RequestCheck()
	{
		// The check runs at the end of the requested pass, which a busy UI delays but cannot starve. With no window to lay
		// out, nothing can be loaded again before it runs.
		if (CoreServices.HasRootWithBounds())
		{
			CoreServices.RequestAdditionalFrame();
		}
		else if (!_isCheckQueued)
		{
			_isCheckQueued = true;
			NativeDispatcher.Main.Enqueue(static () =>
			{
				// A window that got bounds meanwhile loads what it shows in its next pass, which then runs the check.
				if (CoreServices.HasRootWithBounds())
				{
					_isCheckQueued = false;
					CoreServices.RequestAdditionalFrame();
				}
				else
				{
					ReleaseDue(atLayoutPassEnd: false);
				}
			});
		}
	}
}
