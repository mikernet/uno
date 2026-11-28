using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;

#nullable enable

namespace Uno.UI.RuntimeTests.Tests.Windows_UI_Xaml
{
	// The children a DependencyObject propagates its inherited properties to: what registering and unregistering them
	// costs, and which children a propagation reaches when its callbacks add or remove children.
	[TestClass]
	[RunsOnUIThread]
	public partial class Given_DependencyObject_ChildStores
	{
		private static long AllocatedByAddingChildren(int count)
		{
			var children = Enumerable.Range(0, count).Select(_ => new Border()).ToArray();
			var panel = new StackPanel();

			var before = GC.GetAllocatedBytesForCurrentThread();
			foreach (var child in children)
			{
				panel.Children.Add(child);
			}

			return GC.GetAllocatedBytesForCurrentThread() - before;
		}

		private static long AllocatedByRemovingChildren(int count)
		{
			var panel = new StackPanel();
			for (var i = 0; i < count; i++)
			{
				panel.Children.Add(new Border());
			}

			var before = GC.GetAllocatedBytesForCurrentThread();
			while (panel.Children.Count > 0)
			{
				panel.Children.RemoveAt(0);
			}

			return GC.GetAllocatedBytesForCurrentThread() - before;
		}

		// With bytes(n) = a·n + q·n²/2, bytes(2N) - 2·bytes(N) = q·N²: the bytes per pair of children beyond linear growth.
		private static double QuadraticBytesPerPair(Func<int, long> allocated, int n)
		{
			allocated(50); // Warm up.
			var single = allocated(n);
			var twice = allocated(2 * n);
			return (double)(twice - 2 * single) / ((double)n * n);
		}

		[TestMethod]
		public void When_Children_Added_Allocations_Grow_Linearly()
		{
			// Registering a child used to copy the parent's whole list of children: 8 bytes for each pair of children.
			var perPair = QuadraticBytesPerPair(AllocatedByAddingChildren, 2000);
			Assert.IsTrue(perPair < 2, $"{perPair:0.00} bytes per pair of children beyond linear growth.");
		}

		[TestMethod]
		public void When_Children_Removed_Allocations_Grow_Linearly()
		{
			// Unregistering a child used to copy the parent's list of the children left.
			var perPair = QuadraticBytesPerPair(AllocatedByRemovingChildren, 2000);
			Assert.IsTrue(perPair < 2, $"{perPair:0.00} bytes per pair of children beyond linear growth.");
		}

		// A panel of bordered children, recording each child the propagation of a marker DataContext reaches, in order.
		private sealed class Propagation
		{
			private readonly Dictionary<Border, Action> _onReached = new();

			public Propagation(int count)
			{
				Children = Enumerable.Range(0, count).Select(NewChild).ToArray();
				foreach (var child in Children)
				{
					Panel.Children.Add(child);
				}
			}

			public object Marker { get; } = new();

			public StackPanel Panel { get; } = new();

			public Border[] Children { get; }

			public List<Border> Reached { get; } = new();

			public Border NewChild(int tag)
			{
				var child = new Border { Tag = tag };
				child.DataContextChanged += (_, e) =>
				{
					if (ReferenceEquals(e.NewValue, Marker))
					{
						Reached.Add(child);
						if (_onReached.Remove(child, out var action))
						{
							action();
						}
					}
				};

				return child;
			}

			public void OnReached(int index, Action action) => _onReached[Children[index]] = action;

			public void Run() => Panel.DataContext = Marker;

			public Border[] Of(params int[] indexes) => indexes.Select(i => Children[i]).ToArray();
		}

		private static void AssertReached(Border[] expected, Propagation propagation)
			=> CollectionAssert.AreEqual(expected, propagation.Reached.ToArray(), $"Reached: {string.Join(", ", propagation.Reached.Select(c => c.Tag))}");

		private static void AssertDetached(Propagation propagation, params int[] indexes)
		{
			foreach (var index in indexes)
			{
				Assert.IsNull(propagation.Children[index].DataContext, $"Child {index}, removed, doesn't keep the old parent's DataContext");
			}
		}

		[TestMethod]
		[PlatformCondition(ConditionMode.Exclude, RuntimeTestPlatforms.NativeWinUI)] // WinUI's live walk then skips the next child.
		public void When_Propagation_Removes_A_Visited_Child()
		{
			var p = new Propagation(10);
			p.OnReached(3, () => p.Panel.Children.Remove(p.Children[1]));

			p.Run();

			AssertReached(p.Children, p);
			AssertDetached(p, 1);
		}

		[TestMethod]
		public void When_Propagation_Removes_A_Child_Not_Yet_Visited()
		{
			// WinUI's live walk doesn't visit it either (FrameworkElement::PropagateDataContextChanged).
			var p = new Propagation(10);
			p.OnReached(3, () => p.Panel.Children.Remove(p.Children[6]));

			p.Run();

			AssertReached(p.Of(0, 1, 2, 3, 4, 5, 7, 8, 9), p);
			AssertDetached(p, 6);
		}

		[TestMethod]
		public void When_Propagation_Removes_The_Last_Child()
		{
			var p = new Propagation(10);
			p.OnReached(3, () => p.Panel.Children.Remove(p.Children[9]));

			p.Run();

			AssertReached(p.Of(0, 1, 2, 3, 4, 5, 6, 7, 8), p);
			AssertDetached(p, 9);
		}

		[TestMethod]
		[PlatformCondition(ConditionMode.Exclude, RuntimeTestPlatforms.NativeWinUI)] // Uno pushes the inherited value as a child joins.
		public void When_Propagation_Adds_A_Child()
		{
			var p = new Propagation(10);
			var added = p.NewChild(10);
			p.OnReached(3, () => p.Panel.Children.Add(added));

			p.Run();

			AssertReached(p.Of(0, 1, 2, 3).Append(added).Concat(p.Of(4, 5, 6, 7, 8, 9)).ToArray(), p);
		}

		[TestMethod]
		[PlatformCondition(ConditionMode.Exclude, RuntimeTestPlatforms.NativeWinUI)] // Uno pushes the inherited value as a child joins.
		public void When_Propagation_Replaces_The_Last_Child()
		{
			// The added child takes the slot the removed one gave back, inside the loop's count: the loop reaches it
			// and pushes the value it already got as it joined, which raises nothing.
			var p = new Propagation(10);
			var added = p.NewChild(10);
			p.OnReached(3, () =>
			{
				p.Panel.Children.Remove(p.Children[9]);
				p.Panel.Children.Add(added);
			});

#if HAS_UNO
			var lastSlot = p.Children[9].ChildStoreSlot;
#endif

			p.Run();

			AssertReached(p.Of(0, 1, 2, 3).Append(added).Concat(p.Of(4, 5, 6, 7, 8)).ToArray(), p);
			AssertDetached(p, 9);
			Assert.AreSame(p.Marker, added.DataContext);
#if HAS_UNO
			AssertTookTheLastSlot(lastSlot, added);
#endif
		}

		[TestMethod]
		[PlatformCondition(ConditionMode.Exclude, RuntimeTestPlatforms.NativeWinUI)] // WinUI's live walk then skips children.
		public void When_Propagation_Removes_Children_After_The_List_Is_Squeezed()
		{
			// Removing 11 of 20 children squeezes the list into a new array; the loop keeps reading the old one, where
			// the children removed after that are still there.
			var p = new Propagation(20);
			p.OnReached(1, () =>
			{
				for (var i = 2; i <= 13; i++)
				{
					p.Panel.Children.Remove(p.Children[i]);
				}

				p.Panel.Children.Remove(p.Children[15]);
			});

			p.Run();

			AssertReached(p.Of(0, 1, 14, 16, 17, 18, 19), p);
			AssertDetached(p, 12, 13, 15);
		}

		[TestMethod]
		[PlatformCondition(ConditionMode.Exclude, RuntimeTestPlatforms.NativeWinUI)] // Uno pushes the inherited value as a child joins.
		public void When_Propagation_Grows_The_List_Then_Removes_A_Child()
		{
			// Eight children fill an exact array: the add moves them into a larger one.
			var p = new Propagation(8);
			var added = p.NewChild(8);
			p.OnReached(1, () =>
			{
				p.Panel.Children.Add(added);
				p.Panel.Children.Remove(p.Children[5]);
			});

			p.Run();

			AssertReached(p.Of(0, 1).Append(added).Concat(p.Of(2, 3, 4, 6, 7)).ToArray(), p);
			AssertDetached(p, 5);
		}

		[TestMethod]
		[PlatformCondition(ConditionMode.Exclude, RuntimeTestPlatforms.NativeWinUI)] // Uno pushes the inherited value as a child joins.
		public void When_Propagation_Removes_All_Children_Then_Adds_One()
		{
			var p = new Propagation(5);
			var added = p.NewChild(5);
			p.OnReached(1, () =>
			{
				p.Panel.Children.Clear();
				p.Panel.Children.Add(added);
			});

			p.Run();

			AssertReached(p.Of(0, 1).Append(added).ToArray(), p);
			AssertDetached(p, 0, 1, 2, 3, 4);
		}

		[TestMethod]
		[PlatformCondition(ConditionMode.Exclude, RuntimeTestPlatforms.NativeWinUI)] // Uno pushes the inherited value as a child joins.
		[DataRow(3)]
		[DataRow(4)]
		public void When_Propagation_Removes_And_Adds_Back_A_Child(int index)
		{
			// The child gets the value as it joins again, at the end; the last one takes back its own slot.
			var p = new Propagation(5);
			p.OnReached(1, () =>
			{
				p.Panel.Children.Remove(p.Children[index]);
				p.Panel.Children.Add(p.Children[index]);
			});

			p.Run();

			var others = new[] { 2, 3, 4 }.Where(i => i != index).ToArray();
			AssertReached(p.Of(0, 1, index).Concat(p.Of(others)).ToArray(), p);
			Assert.AreSame(p.Marker, p.Children[index].DataContext);
		}

		[TestMethod]
		public void When_Propagation_Moves_A_Child_To_Another_Parent()
		{
			var p = new Propagation(5);
			var other = new StackPanel { DataContext = "other" };
			p.OnReached(1, () =>
			{
				p.Panel.Children.Remove(p.Children[3]);
				other.Children.Add(p.Children[3]);
			});

			p.Run();

			AssertReached(p.Of(0, 1, 2, 4), p);
			Assert.AreEqual("other", p.Children[3].DataContext);
		}

		[TestMethod]
		public void When_Children_Removed_The_Others_Keep_Their_Order()
		{
			var p = new Propagation(10);

			p.Panel.Children.Remove(p.Children[0]);
			p.Panel.Children.Remove(p.Children[5]);
			p.Panel.Children.Remove(p.Children[9]);
			p.Run();

			AssertReached(p.Of(1, 2, 3, 4, 6, 7, 8), p);
			AssertDetached(p, 5);
		}

		[TestMethod]
		public void When_All_Children_Removed_Then_Added_Again()
		{
			var p = new Propagation(3);

			p.Panel.Children.Clear();
			p.Run();
			Assert.AreEqual(0, p.Reached.Count, "No removed child is reached");

			p.Panel.Children.Add(p.Children[2]);
			p.Panel.Children.Add(p.Children[0]);
#if HAS_UNO
			// Uno pushes the inherited value as a child registers; WinUI raises no DataContextChanged when a child joins a parent outside the live tree.
			AssertReached(p.Of(2, 0), p);
#endif

			p.Reached.Clear();
			p.Panel.DataContext = null;
			p.Run();
			AssertReached(p.Of(2, 0), p);
		}
	}
}
