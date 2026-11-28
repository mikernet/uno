#if HAS_UNO
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Documents;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Uno.Disposables;

#nullable enable

namespace Uno.UI.RuntimeTests.Tests.Windows_UI_Xaml
{
	// Each kind of loop over a parent's child stores, with a callback that replaces the parent's last child mid-loop:
	// the added child takes the slot the removed one gave back, inside the loop's count (ten children leave spare slots,
	// so nothing is squeezed or grown), so the loop pushes it the value it already got as it joined. That second push
	// must change nothing and raise nothing.
	public partial class Given_DependencyObject_ChildStores
	{
		private sealed class Changes<T> where T : DependencyObject
		{
			private readonly Dictionary<T, Action> _onReached = new();
			private readonly DependencyProperty _property;
			private readonly Func<T, bool> _isReached;

			public Changes(DependencyProperty property, Func<T, bool> isReached)
			{
				_property = property;
				_isReached = isReached;
			}

			public List<T> Reached { get; } = new();

			public T Watch(T child)
			{
				child.RegisterPropertyChangedCallback(_property, (s, _) =>
				{
					var changed = (T)s;
					if (_isReached(changed))
					{
						Reached.Add(changed);
						if (_onReached.Remove(changed, out var action))
						{
							action();
						}
					}
				});

				return child;
			}

			public void OnReached(T child, Action action) => _onReached[child] = action;

			public void AssertReached(params T[] expected)
				=> CollectionAssert.AreEqual(expected, Reached.ToArray(), $"Reached {Reached.Count} times");
		}

		private static void AssertTookTheLastSlot(int lastSlot, DependencyObject added)
			=> Assert.AreEqual(lastSlot, added.ChildStoreSlot, "The added child is in the slot given back, inside the loop's count");

		private static TextBlock[] TextBlocks(int count) => Enumerable.Range(0, count).Select(i => new TextBlock { Text = i.ToString() }).ToArray();

		[TestMethod]
		public void When_Inherited_Property_Loop_Replaces_The_Last_Child()
		{
			// InvokeCallbacks, for a property both the parent and its children define.
			var host = new ContentControl();
			var changes = new Changes<TextBlock>(TextBlock.FontSizeProperty, t => t.FontSize == 42);
			var children = TextBlocks(10).Select(changes.Watch).ToArray();
			foreach (var child in children)
			{
				child.SetParent(host);
			}

			var added = changes.Watch(new TextBlock());
			var lastSlot = children[9].ChildStoreSlot;
			changes.OnReached(children[1], () =>
			{
				children[9].SetParent(null);
				added.SetParent(host);
			});

			host.FontSize = 42;

			changes.AssertReached(children[..2].Append(added).Concat(children[2..9]).ToArray());
			Assert.AreNotEqual(42, children[9].FontSize);
			Assert.AreEqual(42, added.FontSize);
			AssertTookTheLastSlot(lastSlot, added);
		}

		[TestMethod]
		public void When_Forwarding_Loop_Replaces_The_Last_Child()
		{
			// OnParentPropertyChangedCallback, through a panel that doesn't define the property.
			var panel = new StackPanel();
			var changes = new Changes<TextBlock>(TextBlock.FontSizeProperty, t => t.FontSize == 42);
			var children = TextBlocks(10).Select(changes.Watch).ToArray();
			foreach (var child in children)
			{
				panel.Children.Add(child);
			}

			var host = new UserControl { Content = panel };
			var added = changes.Watch(new TextBlock());
			var lastSlot = children[9].ChildStoreSlot;
			changes.OnReached(children[1], () =>
			{
				panel.Children.Remove(children[9]);
				panel.Children.Add(added);
			});

			host.FontSize = 42;

			changes.AssertReached(children[..2].Append(added).Concat(children[2..9]).ToArray());
			Assert.AreNotEqual(42, children[9].FontSize);
			Assert.AreEqual(42, added.FontSize);
			AssertTookTheLastSlot(lastSlot, added);
		}

		[TestMethod]
		public void When_Forwarded_Reset_Loop_Replaces_The_Last_Child()
		{
			// ResetForwardedInheritedProperties, as the panel leaves the parent whose value it forwarded. The removed
			// child is reset by its own removal; the added one never had the value.
			var panel = new StackPanel();
			var children = TextBlocks(10);
			foreach (var child in children)
			{
				panel.Children.Add(child);
			}

			var host = new UserControl { Content = panel, FontSize = 42 };
			var changes = new Changes<TextBlock>(TextBlock.FontSizeProperty, t => t.FontSize != 42);
			foreach (var child in children)
			{
				changes.Watch(child);
			}

			var added = changes.Watch(new TextBlock());
			var lastSlot = children[9].ChildStoreSlot;
			changes.OnReached(children[1], () =>
			{
				panel.Children.Remove(children[9]);
				panel.Children.Add(added);
			});

			host.Content = null!;

			changes.AssertReached(children[..2].Append(children[9]).Concat(children[2..9]).ToArray());
			Assert.AreEqual(new TextBlock().FontSize, added.FontSize);
			AssertTookTheLastSlot(lastSlot, added);
		}

		[TestMethod]
		public void When_Parent_Joins_A_Tree_And_A_Loop_Replaces_The_Last_Child()
		{
			// The panel gets the forwarded value as it joins, then PropagateInheritedProperties pushes its values to all
			// its children again, the added one included.
			var panel = new StackPanel();
			var changes = new Changes<TextBlock>(TextBlock.FontSizeProperty, t => t.FontSize == 42);
			var children = TextBlocks(10).Select(changes.Watch).ToArray();
			foreach (var child in children)
			{
				panel.Children.Add(child);
			}

			var host = new UserControl { FontSize = 42, DataContext = "context" };
			var added = changes.Watch(new TextBlock());
			var addedContexts = 0;
			added.DataContextChanged += (_, e) => addedContexts += e.NewValue is "context" ? 1 : 0;
			var lastSlot = children[9].ChildStoreSlot;
			changes.OnReached(children[1], () =>
			{
				panel.Children.Remove(children[9]);
				panel.Children.Add(added);
			});

			host.Content = panel;

			changes.AssertReached(children[..2].Append(added).Concat(children[2..9]).ToArray());
			Assert.AreEqual(42, added.FontSize);
			Assert.AreEqual(1, addedContexts);
			Assert.AreEqual("context", added.DataContext);
			Assert.IsNull(children[9].DataContext);
			AssertTookTheLastSlot(lastSlot, added);
		}

		[TestMethod]
		public void When_DataContext_Loop_Under_A_Non_FrameworkElement_Replaces_The_Last_Child()
		{
			// ApplyDataContext: the inline collection, not a FrameworkElement, passes the DataContext to its runs.
			var host = new TextBlock();
			var changes = new Changes<Run>(Run.TextProperty, r => r.Text == "context");
			Run NewRun()
			{
				var run = new Run();
				BindingOperations.SetBinding(run, Run.TextProperty, new Binding());
				return changes.Watch(run);
			}

			var runs = Enumerable.Range(0, 10).Select(_ => NewRun()).ToArray();
			foreach (var run in runs)
			{
				host.Inlines.Add(run);
			}

			var added = NewRun();
			var lastSlot = runs[9].ChildStoreSlot;
			changes.OnReached(runs[1], () =>
			{
				host.Inlines.Remove(runs[9]);
				host.Inlines.Add(added);
			});

			host.DataContext = "context";

			changes.AssertReached(runs[..2].Append(added).Concat(runs[2..9]).ToArray());
			Assert.AreNotEqual("context", runs[9].Text);
			Assert.AreEqual("context", added.Text);
			AssertTookTheLastSlot(lastSlot, added);
		}

		// A property whose coercion runs on every push, an equal value included, so a test sees each push.
		private sealed partial class PushBox : Grid
		{
			[ThreadStatic]
			private static Action<PushBox>? t_onPush;

			public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
				"Value",
				typeof(int),
				typeof(PushBox),
				new FrameworkPropertyMetadata(
					0,
					FrameworkPropertyMetadataOptions.Inherits,
					new PropertyChangedCallback((d, _) => ((PushBox)d).Changes++),
					new CoerceValueCallback((d, value, _) =>
					{
						var box = (PushBox)d;
						box.Pushes++;
						t_onPush?.Invoke(box);
						return value;
					})));

			public int Value
			{
				get => (int)GetValue(ValueProperty);
				set => SetValue(ValueProperty, value);
			}

			public int Pushes { get; private set; }

			public int Changes { get; private set; }

			public static IDisposable OnPush(Action<PushBox> onPush)
			{
				t_onPush = onPush;
				return Disposable.Create(() => t_onPush = null);
			}
		}

		[TestMethod]
		public void When_Forwarded_Values_Loop_Replaces_The_Last_Child()
		{
			// PropagateInheritedNonLocalProperties: as the panel joins a parent, it pushes the values it forwards to all its
			// children again, after they got them through the forwarding loop. The coercion sees that second push.
			var panel = new StackPanel();
			var children = Enumerable.Range(0, 10).Select(_ => new PushBox()).ToArray();
			foreach (var child in children)
			{
				panel.Children.Add(child);
			}

			var host = new PushBox { Value = 42 };
			var added = new PushBox();
			var lastSlot = children[9].ChildStoreSlot;
			var secondPushes = new List<PushBox>();
			using var _ = PushBox.OnPush(box =>
			{
				if (box == host)
				{
					return;
				}

				if (box.Pushes == 2)
				{
					secondPushes.Add(box);
				}

				if (box == children[1] && box.Pushes == 2)
				{
					panel.Children.Remove(children[9]);
					panel.Children.Add(added);
				}
			});

			host.Children.Add(panel);

			CollectionAssert.AreEqual(children[..9].Append(added).ToArray(), secondPushes.ToArray(), "The second pushes");
			foreach (var child in children[..9])
			{
				Assert.AreEqual((42, 2, 1), (child.Value, child.Pushes, child.Changes));
			}

			Assert.AreEqual(1, children[9].Pushes, "The removed child isn't reached by the second loop");
			Assert.AreEqual(0, children[9].Value);
			Assert.AreEqual((42, 2, 1), (added.Value, added.Pushes, added.Changes), "The added child: pushed as it joined and again by the loop, changed once");
			AssertTookTheLastSlot(lastSlot, added);
		}
	}
}
#endif
