#nullable enable

using System;
using System.Runtime.CompilerServices;

namespace Microsoft.UI.Xaml;

/// <summary>
/// The child stores a <see cref="DependencyObject"/> propagates its inherited properties to: added and removed in
/// place, and walked by loops that may add or remove children from their callbacks.
/// </summary>
/// <remarks>
/// <para>
/// An add appends; a removal clears the child's slot, which the child remembers (<see cref="DependencyObject.ChildStoreSlot"/>),
/// so both are O(1) and no other child moves. Removing the last used slot gives back the trailing empty slots, so a
/// parent that swaps its last child (a <c>ContentPresenter</c>, a <c>Border</c>, a <c>Frame</c>) reuses its storage,
/// and a list whose last child leaves keeps its array, by then eight slots or fewer, for the next add.
/// Past eight slots, once holes outnumber children or children fill at most a quarter of the array, the children are
/// squeezed into a new array half again as large as they need; a full array grows into a new one. Between two resizes
/// a list takes a number of adds or removals proportional to its size, so a steady add-and-remove never resizes. An
/// array is never shrunk or reordered in place, so a loop keeps reading the array it started with.
/// </para>
/// <para>
/// The rule for a loop: it visits at most the slots it started with, in order; it never skips a live child that was
/// there; it never visits a removed one (after a squeeze or a growth, it checks that the child is still at its slot in
/// the current array). A child added during the loop, or removed and added back, is normally not visited; if it lands
/// in a tail slot given back during the loop, it may be visited, and pushed the loop's value. That is the value it got
/// when it joined, unless the parent's value changed again during the loop: then it ends with the loop's older value,
/// as do the children the loop visits after that change.
/// </para>
/// <para>
/// The owner's thread only, like the rest of the store.
/// </para>
/// </remarks>
internal sealed class ChildStoreList
{
	// Arrays of up to this many slots grow one slot at a time and are never squeezed or shrunk.
	private const int SmallCapacity = 8;

	private DependencyObject?[] _items;
	private int _count; // Slots used, empty ones included.
	private int _live;

	private ChildStoreList(DependencyObject child)
	{
		_items = [child];
		_count = 1;
		_live = 1;
		child.ChildStoreSlot = 0;
	}

	internal int Count => _count;

	internal int LiveCount => _live;

	internal int Capacity => _items.Length;

	public static bool HasAny(ChildStoreList? list) => list is not null && list._live != 0;

	public static void Add(ref ChildStoreList? field, DependencyObject child)
	{
		if (field is not { } list)
		{
			field = new ChildStoreList(child);
			return;
		}

		if (list._count == list._items.Length)
		{
			var live = list._live;
			list.MoveInto(new DependencyObject?[live < SmallCapacity ? live + 1 : live * 2]);
		}

		var slot = list._count++;
		list._items[slot] = child;
		list._live++;
		child.ChildStoreSlot = slot;
	}

	public void Remove(DependencyObject child)
	{
		var items = _items;
		var slot = child.ChildStoreSlot;
		if ((uint)slot >= (uint)_count || !ReferenceEquals(items[slot], child))
		{
			// A child's slot is exact for the list it is in: searching only finds that it isn't in this one.
			slot = Array.IndexOf(items, child, 0, _count);
			if (slot < 0)
			{
				return;
			}
		}

		items[slot] = null;
		if (--_live == 0)
		{
			// Kept for the next add: with one child left, the shrink below had already brought it to eight slots or fewer.
			_count = 0;
			return;
		}

		if (slot == _count - 1)
		{
			var count = slot;
			while (items[count - 1] is null)
			{
				count--;
			}

			_count = count;
		}

		// A list shrinking from the back leaves no holes, so the array is also checked against the children it holds.
		var live = _live;
		if ((_count > SmallCapacity && live * 2 < _count)
			|| (items.Length > SmallCapacity && live * 4 <= items.Length))
		{
			MoveInto(new DependencyObject?[live + live / 2]);
		}
	}

	// Into a new array, always: a loop may be reading the current one.
	private void MoveInto(DependencyObject?[] items)
	{
		var source = _items;
		var j = 0;
		for (var i = 0; i < _count; i++)
		{
			if (source[i] is { } child)
			{
				items[j] = child;
				child.ChildStoreSlot = j;
				j++;
			}
		}

		_items = items;
		_count = j;
	}

	internal bool Contains(DependencyObject child)
	{
		var slot = child.ChildStoreSlot;
		return (uint)slot < (uint)_count && ReferenceEquals(_items[slot], child);
	}

	public static Enumerator Enumerate(ChildStoreList? list) => new(list);

	public struct Enumerator
	{
		private readonly ChildStoreList? _list;
		private readonly DependencyObject?[]? _items;
		private readonly int _count;
		private int _index;
		private DependencyObject? _current;

		internal Enumerator(ChildStoreList? list)
		{
			_list = list;
			_items = list?._items;
			_count = list?._count ?? 0;
			_index = -1;
			_current = null;
		}

		public readonly DependencyObject Current => _current!;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool MoveNext()
		{
			var items = _items;
			var index = _index;
			while (++index < _count)
			{
				// A removal clears the slot in the current array only, so a loop on an older one asks the list.
				if (items![index] is { } child
					&& (ReferenceEquals(items, _list!._items) || _list.Contains(child)))
				{
					_index = index;
					_current = child;
					return true;
				}
			}

			_index = index;
			return false;
		}

		public readonly Enumerator GetEnumerator() => this;
	}
}
