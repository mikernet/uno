﻿using System;
using System.Collections.Generic;
using System.Text;
using Uno;
using Uno.Extensions;
using Windows.Foundation;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace Windows.UI.Xaml
{
	public partial class UIElement : DependencyObject
	{
		public UIElement()
		{
			Initialize();
			InitializePointers();
		}

		private Rect _arranged;

		public string Name { get; set; }

		/// <summary>
		/// Determines if InvalidateMeasure has been called
		/// </summary>
		internal bool IsMeasureDirty => false;

		/// <summary>
		/// This is for compatibility - not implemented yet on this platform
		/// </summary>
		internal bool IsMeasureOrMeasureDirtyPath => IsMeasureDirty;

		/// <summary>
		/// Determines if InvalidateArrange has been called
		/// </summary>
		internal bool IsArrangeDirty => false;

		internal bool IsPointerCaptured { get; set; }

		public int MeasureCallCount { get; protected set; }
		public int ArrangeCallCount { get; protected set; }

		public Size? RequestedDesiredSize { get; set; }
		public Size AvailableMeasureSize { get; protected set; }

		public Rect Arranged
		{
			get => _arranged;
			set
			{
				ArrangeCallCount++;
				_arranged = value;
			}
		}

		public Func<Size, Size> DesiredSizeSelector { get; set; }

		public IntPtr Handle { get; set; }

		internal Windows.Foundation.Point GetPosition(Point position, global::Windows.UI.Xaml.UIElement relativeTo)
		{
			throw new NotSupportedException();
		}

		public string ShowLocalVisualTree(int fromHeight = 1000) => Uno.UI.ViewExtensions.ShowLocalVisualTree(this, fromHeight);

		//TODO Uno: This is currently just a stub, should be implemented properly for tests
		[NotImplemented]
		internal void AddChild(UIElement child, int? index = null)
		{
			child.SetParent(this);
		}
	}
}
