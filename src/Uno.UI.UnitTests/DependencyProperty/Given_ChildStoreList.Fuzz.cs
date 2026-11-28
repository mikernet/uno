#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Uno.UI.Tests.DependencyPropertyTests;

public partial class Given_ChildStoreList
{
	[TestMethod]
	public void When_Loops_Run_Under_Random_Reentrant_Changes_Then_The_Loop_Rule_Holds()
	{
		// Two lists and a model of each. Loops change the lists from inside: adds, children added back, removals (of the
		// last child too), moves to the other list, clears, removals of children a list doesn't hold, and nested loops.
		var fuzz = new Fuzz();
		for (var seed = 0; seed < 2000; seed++)
		{
			fuzz.Run(seed);
		}

		Assert.AreEqual(0, fuzz.Violations, $"{fuzz.Violations} violations in {fuzz.Loops:N0} loops: {string.Join("; ", fuzz.FirstViolations)}");
		Assert.IsTrue(fuzz.Loops > 5000, $"{fuzz.Loops} loops");
		Assert.IsTrue(fuzz.AddedVisits > 0, "Some loops reach a child added in a given-back tail slot");
	}

	private sealed class Fuzz
	{
		private readonly Dictionary<Item, int> _owner = new();
		private readonly List<Item> _pool = new();
		private readonly List<Action<int, Item>> _onAdd = new();
		private readonly List<Action<int, Item>> _onRemove = new();
		private Random _random = new(0);
		private ChildStoreList?[] _lists = new ChildStoreList?[2];
		private HashSet<Item>[] _model = [new(), new()];
		private int _nextId;

		public int Loops { get; private set; }

		public int AddedVisits { get; private set; }

		public int Violations { get; private set; }

		public List<string> FirstViolations { get; } = new();

		public void Run(int seed)
		{
			_random = new Random(seed);
			_lists = new ChildStoreList?[2];
			_model = [new(), new()];
			_owner.Clear();
			_pool.Clear();

			var initial = _random.Next(4) == 0 ? _random.Next(0, 60) : _random.Next(0, 12);
			for (var i = 0; i < initial; i++)
			{
				Add(_random.Next(5) == 0 ? 1 : 0, NewItem());
			}

			for (var i = _random.Next(initial + 1); i > 0 && _model[0].Count > 0; i--)
			{
				Remove(0, _model[0].ElementAt(_random.Next(_model[0].Count)));
			}

			Check(seed);
			for (var i = 0; i < 3; i++)
			{
				Loop(0, 0, seed);
			}
		}

		private Item NewItem()
		{
			var item = new Item(_nextId++);
			_pool.Add(item);
			return item;
		}

		private void Add(int list, Item child)
		{
			ChildStoreList.Add(ref _lists[list], child);
			_model[list].Add(child);
			_owner[child] = list;
			foreach (var onAdd in _onAdd.ToArray())
			{
				onAdd(list, child);
			}
		}

		private void Remove(int list, Item child)
		{
			_lists[list]!.Remove(child);
			_model[list].Remove(child);
			_owner.Remove(child);
			foreach (var onRemove in _onRemove.ToArray())
			{
				onRemove(list, child);
			}
		}

		private void Mutate(int depth, int seed)
		{
			var operations = _random.Next(_random.Next(4) == 0 ? 12 : 3);
			for (var k = 0; k < operations; k++)
			{
				var list = _random.Next(4) == 0 ? 1 : 0;
				switch (_random.Next(7))
				{
					case 0:
					case 1:
						var child = _pool.Count > 0 && _random.Next(3) == 0 ? _pool[_random.Next(_pool.Count)] : NewItem();
						if (!_owner.ContainsKey(child))
						{
							Add(list, child);
						}
						break;

					case 2:
					case 3:
					case 4:
						if (_model[list].Count > 0)
						{
							var live = _model[list].ToArray();
							Remove(list, _random.Next(3) == 0 ? live.MaxBy(c => c.ChildStoreSlot)! : live[_random.Next(live.Length)]);
						}
						break;

					case 5:
						if (_model[list].Count > 0)
						{
							var moved = _model[list].ElementAt(_random.Next(_model[list].Count));
							Remove(list, moved);
							Add(1 - list, moved);
						}
						break;

					default:
						if (depth < 3)
						{
							Loop(0, depth + 1, seed);
						}
						break;
				}

				if (_random.Next(40) == 0)
				{
					foreach (var live in _model[list].ToArray())
					{
						Remove(list, live);
					}
				}

				if (_random.Next(20) == 0 && _pool.Count > 0 && _lists[list] is { } target)
				{
					// A removal of a child the list doesn't hold, as a second dispose would do.
					var stranger = _pool[_random.Next(_pool.Count)];
					if (!_owner.ContainsKey(stranger))
					{
						target.Remove(stranger);
					}
				}

				Check(seed);
			}
		}

		private void Loop(int list, int depth, int seed)
		{
			Loops++;
			var started = _model[list].OrderBy(c => c.ChildStoreSlot).ToList();
			var position = started.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i);
			var removed = new HashSet<Item>();
			var added = new HashSet<Item>();
			Action<int, Item> onAdd = (l, c) =>
			{
				if (l == list)
				{
					added.Add(c);
				}
			};
			Action<int, Item> onRemove = (l, c) =>
			{
				if (l == list)
				{
					removed.Add(c);
				}
			};
			_onAdd.Add(onAdd);
			_onRemove.Add(onRemove);

			var visited = new HashSet<Item>();
			var last = -1;
			foreach (var visit in ChildStoreList.Enumerate(_lists[list]))
			{
				var child = (Item)visit;
				if (!_model[list].Contains(child))
				{
					Fail(seed, $"visited {child}, which isn't in the list");
				}
				else if (position.TryGetValue(child, out var at) && !removed.Contains(child))
				{
					if (at <= last)
					{
						Fail(seed, $"visited {child} out of order");
					}

					last = at;
				}
				else if (added.Contains(child))
				{
					AddedVisits++;
				}
				else
				{
					Fail(seed, $"visited {child}, which was neither there at the start nor added");
				}

				visited.Add(child);
				if (_random.Next(2) == 0)
				{
					Mutate(depth, seed);
				}
			}

			_onAdd.Remove(onAdd);
			_onRemove.Remove(onRemove);
			foreach (var child in started)
			{
				if (!removed.Contains(child) && !visited.Contains(child))
				{
					Fail(seed, $"skipped {child}, which was there at the start and never removed");
				}
			}
		}

		private void Check(int seed)
		{
			for (var list = 0; list < 2; list++)
			{
				var model = _model[list];
				if (_lists[list] is not { } actual)
				{
					if (model.Count != 0)
					{
						Fail(seed, $"list {list} is gone with {model.Count} children");
					}

					continue;
				}

				if (actual.LiveCount != model.Count)
				{
					Fail(seed, $"list {list} counts {actual.LiveCount} children, not {model.Count}");
				}

				foreach (var child in model)
				{
					if (!actual.Contains(child))
					{
						Fail(seed, $"{child} isn't at its slot in list {list}");
					}
				}

				var walked = Walk(actual);
				if (walked.Length != model.Count || walked.Any(c => !model.Contains(c)))
				{
					Fail(seed, $"a walk of list {list} doesn't give its children");
				}

				var lastSlot = walked.Length > 0 ? walked[^1].ChildStoreSlot : -1;
				if (actual.Count != lastSlot + 1)
				{
					Fail(seed, $"list {list} counts {actual.Count} slots, past its last child at {lastSlot}");
				}
			}
		}

		private void Fail(int seed, string message)
		{
			if (Violations++ < 3)
			{
				FirstViolations.Add($"seed {seed}: {message}");
			}
		}
	}
}
