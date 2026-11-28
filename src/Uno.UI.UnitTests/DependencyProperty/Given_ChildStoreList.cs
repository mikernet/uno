#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Uno.UI.Tests.DependencyPropertyTests;

[UITestClass]
public partial class Given_ChildStoreList
{
	private sealed class Item(int id) : DependencyObject
	{
		public int Id { get; } = id;

		public override string ToString() => Id.ToString();
	}

	private static Item[] NewItems(int count) => Enumerable.Range(0, count).Select(i => new Item(i)).ToArray();

	private static ChildStoreList? ListOf(IEnumerable<Item> items)
	{
		ChildStoreList? field = null;
		foreach (var item in items)
		{
			ChildStoreList.Add(ref field, item);
		}

		return field;
	}

	private static Item[] Walk(ChildStoreList? list)
	{
		var walked = new List<Item>();
		foreach (var child in ChildStoreList.Enumerate(list))
		{
			walked.Add((Item)child);
		}

		return walked.ToArray();
	}

	private static Item[] Rest(ref ChildStoreList.Enumerator enumerator)
	{
		var walked = new List<Item>();
		while (enumerator.MoveNext())
		{
			walked.Add((Item)enumerator.Current);
		}

		return walked.ToArray();
	}

	[TestMethod]
	public void When_Added_Then_Appended_In_Order()
	{
		var items = NewItems(100);
		var list = ListOf(items);

		CollectionAssert.AreEqual(items, Walk(list));
		Assert.AreEqual(100, list!.Count);
		for (var i = 0; i < items.Length; i++)
		{
			Assert.AreEqual(i, items[i].ChildStoreSlot);
		}
	}

	[TestMethod]
	[DataRow(1)]
	[DataRow(2)]
	[DataRow(8)]
	public void When_Few_Children_Then_The_Array_Is_Exact(int count)
		=> Assert.AreEqual(count, ListOf(NewItems(count))!.Capacity);

	[TestMethod]
	public void When_Removed_Then_The_Slot_Is_Cleared_And_No_Child_Moves()
	{
		var items = NewItems(8);
		var list = ListOf(items);

		list!.Remove(items[2]);
		list!.Remove(items[5]);

		CollectionAssert.AreEqual(new[] { 0, 1, 3, 4, 6, 7 }.Select(i => items[i]).ToArray(), Walk(list));
		Assert.AreEqual(8, list!.Count, "The cleared slots stay counted");
		Assert.AreEqual(6, list.LiveCount);
		Assert.AreEqual(7, items[7].ChildStoreSlot);
	}

	[TestMethod]
	public void When_Added_After_A_Removal_Inside_Then_The_Hole_Is_Not_Refilled()
	{
		// Nine children leave spare slots, so the add doesn't squeeze the list.
		var items = NewItems(9);
		var list = ListOf(items);
		var added = new Item(10);

		list!.Remove(items[1]);
		ChildStoreList.Add(ref list, added);

		// A slot only goes from a child to empty, or from empty to a child at or past the count.
		Assert.AreEqual(9, added.ChildStoreSlot);
		CollectionAssert.AreEqual(items.Where((_, i) => i != 1).Append(added).ToArray(), Walk(list));
	}

	[TestMethod]
	public void When_The_Last_Child_Is_Removed_Then_Its_Slot_Is_Reused()
	{
		// Tail reuse: the content swap of a parent that replaces its last child allocates nothing.
		var items = NewItems(3);
		var list = ListOf(items);
		var capacity = list!.Capacity;
		var added = new Item(10);

		list!.Remove(items[2]);
		ChildStoreList.Add(ref list, added);

		Assert.AreEqual(2, added.ChildStoreSlot);
		Assert.AreEqual(capacity, list!.Capacity);
		CollectionAssert.AreEqual(new[] { items[0], items[1], added }, Walk(list));
	}

	[TestMethod]
	public void When_The_Last_Child_Is_Removed_Then_The_Trailing_Holes_Are_Given_Back()
	{
		var items = NewItems(6);
		var list = ListOf(items);

		list!.Remove(items[3]);
		list!.Remove(items[4]);
		Assert.AreEqual(6, list!.Count);

		list!.Remove(items[5]);
		Assert.AreEqual(3, list!.Count);
	}

	[TestMethod]
	[DataRow(1)]
	[DataRow(8)]
	public void When_A_Small_List_Is_Emptied_Then_It_Keeps_Its_Storage(int count)
	{
		var items = NewItems(count);
		var list = ListOf(items);
		var holder = list;
		var capacity = list!.Capacity;

		foreach (var item in items)
		{
			list!.Remove(item);
		}

		Assert.AreSame(holder, list);
		Assert.IsFalse(ChildStoreList.HasAny(list));
		Assert.AreEqual(0, list!.Count);

		var added = new Item(10);
		ChildStoreList.Add(ref list, added);
		Assert.AreSame(holder, list);
		Assert.AreEqual(capacity, list!.Capacity);
		Assert.AreEqual(0, added.ChildStoreSlot);
	}

	[TestMethod]
	[DataRow(1)]
	[DataRow(9)]
	[DataRow(100)]
	public void When_Shrunk_From_The_Back_Then_The_Array_Shrinks(int kept)
	{
		// Removing from the back leaves no holes: the array is shrunk on its size, so a parent that once held many
		// children doesn't keep their array.
		var items = NewItems(1000);
		var list = ListOf(items);
		Assert.AreEqual(1024, list!.Capacity);

		for (var i = items.Length - 1; i >= kept; i--)
		{
			list.Remove(items[i]);
		}

		Assert.IsTrue(list.Capacity <= 8 || list.Capacity < 4 * kept, $"{list.Capacity} slots for {kept} children");
		CollectionAssert.AreEqual(items.Take(kept).ToArray(), Walk(list));
	}

	[TestMethod]
	public void When_Emptied_From_The_Back_Then_A_Small_Array_Is_Kept()
	{
		var items = NewItems(1000);
		var list = ListOf(items);
		var holder = list;

		foreach (var item in items.Reverse())
		{
			list!.Remove(item);
		}

		Assert.AreSame(holder, list);
		Assert.IsFalse(ChildStoreList.HasAny(list));
		Assert.IsTrue(list!.Capacity <= 8, $"{list.Capacity} slots kept");
	}

	[TestMethod]
	[DataRow(8)]
	[DataRow(16)]
	[DataRow(100)]
	[DataRow(1000)]
	public void When_Adding_And_Removing_At_A_Steady_Size_Then_Nothing_Is_Allocated(int count)
	{
		// The sizes include the growth thresholds (8 and 16 fill their arrays): the shrink threshold sits far enough
		// below them that one child more and one less never resize.
		var items = NewItems(count);
		var list = ListOf(items)!;
		var extra = new Item(-1);

		void Cycle()
		{
			ChildStoreList? field = list;
			ChildStoreList.Add(ref field, extra);
			list.Remove(extra);
			list.Remove(items[^1]);
			ChildStoreList.Add(ref field, items[^1]);
		}

		Cycle();
		var before = GC.GetAllocatedBytesForCurrentThread();
		for (var i = 0; i < 1000; i++)
		{
			Cycle();
		}

		Assert.AreEqual(0, GC.GetAllocatedBytesForCurrentThread() - before);
		CollectionAssert.AreEqual(items, Walk(list));
	}

	[TestMethod]
	public void When_Grown_And_Shrunk_Repeatedly_Then_Each_Cycle_Costs_The_Same()
	{
		const int Count = 1000;
		var items = NewItems(Count);
		var list = ListOf(items.Take(1));

		long Cycle()
		{
			var before = GC.GetAllocatedBytesForCurrentThread();
			for (var i = 1; i < Count; i++)
			{
				ChildStoreList.Add(ref list, items[i]);
			}

			for (var i = Count - 1; i >= 1; i--)
			{
				list!.Remove(items[i]);
			}

			return GC.GetAllocatedBytesForCurrentThread() - before;
		}

		var first = Cycle();
		var second = Cycle();
		var third = Cycle();

		Assert.AreEqual(second, third);
		// The arrays of one growth and one shrink: geometric series, a few times the largest array (1,024 slots).
		Assert.IsTrue(second < 8 * 8 * 1024, $"{second:N0} bytes per cycle");
		Assert.IsTrue(first < 8 * 8 * 1024, $"{first:N0} bytes for the first cycle");
	}

	[TestMethod]
	[DataRow(8, 8, 3)]
	[DataRow(9, 4, 6)]
	[DataRow(10, 4, 6)]
	public void When_Fewer_Than_Half_The_Slots_Are_Used_Then_The_List_Is_Squeezed(int count, int liveAtSqueeze, int squeezedCapacity)
	{
		// Removing from the front, so that no tail is given back.
		var items = NewItems(count);
		var list = ListOf(items);

		for (var removed = 1; removed < count; removed++)
		{
			list!.Remove(items[removed - 1]);
			var live = count - removed;
			if (count > 8 && live == liveAtSqueeze)
			{
				Assert.AreEqual(live, list!.Count, $"Squeezed with {live} children left");
				Assert.AreEqual(squeezedCapacity, list.Capacity);
				break;
			}

			Assert.AreEqual(count, list!.Count, $"Not squeezed with {live} children left");
		}

		if (count <= 8)
		{
			Assert.AreEqual(liveAtSqueeze, list!.Capacity, "A small list is never squeezed");
		}
	}

	[TestMethod]
	public void When_Squeezed_Then_The_Survivors_Slots_Follow()
	{
		var items = NewItems(16);
		var list = ListOf(items);
		for (var i = 0; i < 9; i++)
		{
			list!.Remove(items[i]);
		}

		var survivors = items.Skip(9).ToArray();
		CollectionAssert.AreEqual(survivors, Walk(list));
		Assert.AreEqual(survivors.Length, list!.Count);
		for (var i = 0; i < survivors.Length; i++)
		{
			Assert.AreEqual(i, survivors[i].ChildStoreSlot);
		}

		list!.Remove(survivors[3]);
		Assert.AreEqual(survivors.Length - 1, list!.LiveCount, "Removed through its new slot");
		CollectionAssert.AreEqual(survivors.Where((_, i) => i != 3).ToArray(), Walk(list));
	}

	[TestMethod]
	public void When_Walking_Then_A_Removed_Child_Not_Yet_Reached_Is_Skipped()
	{
		var items = NewItems(5);
		var list = ListOf(items);

		var enumerator = ChildStoreList.Enumerate(list);
		Assert.IsTrue(enumerator.MoveNext());
		list!.Remove(items[2]);

		CollectionAssert.AreEqual(new[] { items[1], items[3], items[4] }, Rest(ref enumerator));
	}

	[TestMethod]
	public void When_Walking_Then_A_Child_Added_Past_The_Count_Is_Not_Reached()
	{
		var items = NewItems(4);
		var list = ListOf(items);

		var enumerator = ChildStoreList.Enumerate(list);
		Assert.IsTrue(enumerator.MoveNext());
		list!.Remove(items[1]);
		ChildStoreList.Add(ref list, new Item(10));

		CollectionAssert.AreEqual(new[] { items[2], items[3] }, Rest(ref enumerator));
	}

	[TestMethod]
	public void When_Walking_Then_A_Child_Added_In_A_Given_Back_Tail_Slot_May_Be_Reached()
	{
		// The accepted consequence of tail reuse: such a child already holds the values a propagation pushes.
		var items = NewItems(4);
		var list = ListOf(items);
		var added = new Item(10);

		var enumerator = ChildStoreList.Enumerate(list);
		Assert.IsTrue(enumerator.MoveNext());
		list!.Remove(items[3]);
		ChildStoreList.Add(ref list, added);

		CollectionAssert.AreEqual(new[] { items[1], items[2], added }, Rest(ref enumerator));
	}

	[TestMethod]
	public void When_Squeezed_While_Walking_Then_The_Walk_Keeps_Its_Array()
	{
		var items = NewItems(16);
		var list = ListOf(items);

		var enumerator = ChildStoreList.Enumerate(list);
		Assert.IsTrue(enumerator.MoveNext());
		for (var i = 1; i < 10; i++)
		{
			list!.Remove(items[i]);
		}

		Assert.AreEqual(7, list!.Count, "Squeezed");
		CollectionAssert.AreEqual(items.Skip(10).ToArray(), Rest(ref enumerator), "The survivors, though they moved");
	}

	[TestMethod]
	public void When_Removed_After_A_Squeeze_While_Walking_Then_It_Is_Not_Reached()
	{
		// The walk reads the array from before the squeeze, where the child's slot is still set.
		var items = NewItems(16);
		var list = ListOf(items);

		var enumerator = ChildStoreList.Enumerate(list);
		Assert.IsTrue(enumerator.MoveNext());
		for (var i = 1; i < 10; i++)
		{
			list!.Remove(items[i]);
		}

		list!.Remove(items[12]);

		CollectionAssert.AreEqual(new[] { 10, 11, 13, 14, 15 }.Select(i => items[i]).ToArray(), Rest(ref enumerator));
	}

	[TestMethod]
	public void When_Grown_While_Walking_Then_The_Walk_Keeps_Its_Array()
	{
		var items = NewItems(8);
		var list = ListOf(items);
		Assert.AreEqual(8, list!.Capacity);

		var enumerator = ChildStoreList.Enumerate(list);
		Assert.IsTrue(enumerator.MoveNext());
		var added = new Item(10);
		ChildStoreList.Add(ref list, added);
		Assert.AreEqual(16, list!.Capacity, "Grown");
		list!.Remove(items[5]);

		CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 6, 7 }.Select(i => items[i]).ToArray(), Rest(ref enumerator));
		CollectionAssert.AreEqual(items.Where((_, i) => i != 5).Append(added).ToArray(), Walk(list));
	}

	[TestMethod]
	public void When_A_Child_Not_In_The_List_Is_Removed_Then_Nothing_Changes()
	{
		var items = NewItems(3);
		var list = ListOf(items);
		var other = ListOf(NewItems(2));
		var stranger = new Item(10);
		ChildStoreList.Add(ref other, stranger);

		list!.Remove(stranger);

		CollectionAssert.AreEqual(items, Walk(list));
	}
}
