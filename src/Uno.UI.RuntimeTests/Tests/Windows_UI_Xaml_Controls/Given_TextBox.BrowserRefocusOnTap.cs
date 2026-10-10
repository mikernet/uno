#if __SKIA__
using System;
using System.Threading.Tasks;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Private.Infrastructure;
using Uno.Disposables;
using Uno.Extensions;
using Uno.UI.RuntimeTests.Helpers;
using Uno.UI.Toolkit.DevTools.Input;
using Windows.UI.Input.Preview.Injection;
using static Private.Infrastructure.TestServices;
using static Uno.UI.RuntimeTests.Tests.Windows_UI_Xaml_Automation.WasmSemanticDomHelper;

namespace Uno.UI.RuntimeTests.Tests.Windows_UI_Xaml_Controls;

/// <summary>
/// By default the soft keyboard of iOS only comes up for an input that is focused while the user is interacting
/// with the page. These tests make the hidden native input's bridge take the browser for an iOS one.
/// </summary>
public partial class Given_TextBox
{
	private const string HiddenInputBridge = "globalThis.Uno.UI.Runtime.Skia.BrowserInvisibleTextBoxViewExtension";

	// Past the delay within which the gesture recognizer takes a second tap for a double tap.
	private static readonly TimeSpan MultiTapDelay = TimeSpan.FromMicroseconds(GestureRecognizer.MultiTapMaxDelayMicroseconds) + TimeSpan.FromMilliseconds(100);

	[TestMethod]
	[RunsOnUIThread]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	public async Task When_Focused_TextBox_Tapped_On_iOS_Then_Input_Focused_Again()
	{
		var injector = InputInjector.TryCreate() ?? throw new InvalidOperationException("Failed to init the InputInjector");
		using var finger = injector.GetFinger();
		var SUT = new TextBox();
		await UITestHelper.Load(SUT);

		using var platform = SimulateIOS(true);

		// Focused from code, the input has no soft keyboard on iOS.
		await FocusHiddenInput(SUT);
		CountHiddenInputFocusCalls();

		// The tap finds the TextBox focused already. The input is focused again for it, and not blurred.
		finger.Press(SUT.GetAbsoluteBoundsRect().GetCenter());
		finger.Release();
		await WindowHelper.WaitForIdle();

		var (blurs, focuses) = GetHiddenInputFocusCalls();
		Assert.AreEqual(0, blurs);
		Assert.IsTrue(focuses > 0, "The tap should focus the hidden input again.");
		Assert.IsTrue(IsHiddenInputFocused());
		Assert.AreEqual(FocusState.Pointer, SUT.FocusState);

		// So is it for any later tap, which no change of focus state comes with. The mousedown that trails a
		// touch is kept from blurring it.
		await Task.Delay(MultiTapDelay);
		CountHiddenInputFocusCalls();
		DisarmTrailingClickGuard();
		finger.Press(SUT.GetAbsoluteBoundsRect().GetCenter());
		finger.Release();
		await WindowHelper.WaitForIdle();

		(blurs, focuses) = GetHiddenInputFocusCalls();
		Assert.AreEqual(0, blurs);
		Assert.AreEqual(1, focuses);
		Assert.IsTrue(IsTrailingClickGuardArmed());
	}

	[TestMethod]
	[RunsOnUIThread]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	public async Task When_Focused_TextBox_Tapped_Off_iOS_Then_Input_Left_Alone()
	{
		var injector = InputInjector.TryCreate() ?? throw new InvalidOperationException("Failed to init the InputInjector");
		using var finger = injector.GetFinger();
		var SUT = new TextBox();
		await UITestHelper.Load(SUT);

		using var platform = SimulateIOS(false);

		// Other browsers show the keyboard for a focus from code as they see fit.
		await FocusHiddenInput(SUT);
		CountHiddenInputFocusCalls();
		DisarmTrailingClickGuard();

		finger.Press(SUT.GetAbsoluteBoundsRect().GetCenter());
		finger.Release();
		await WindowHelper.WaitForIdle();
		await Task.Delay(MultiTapDelay);
		finger.Press(SUT.GetAbsoluteBoundsRect().GetCenter());
		finger.Release();
		await WindowHelper.WaitForIdle();

		Assert.AreEqual((0, 0), GetHiddenInputFocusCalls());
		Assert.IsTrue(IsHiddenInputFocused());
	}

	[TestMethod]
	[RunsOnUIThread]
	[PlatformCondition(ConditionMode.Include, RuntimeTestPlatforms.SkiaWasm)]
	public async Task When_Another_TextBox_Tapped_On_iOS_Then_Input_Focused_Again()
	{
		var injector = InputInjector.TryCreate() ?? throw new InvalidOperationException("Failed to init the InputInjector");
		using var finger = injector.GetFinger();
		var first = new TextBox();
		var SUT = new TextBox();
		await UITestHelper.Load(new StackPanel { Children = { first, SUT } });

		using var platform = SimulateIOS(true);

		// The input is handed over from one TextBox to the other without losing focus, so the tap that moves
		// focus has to focus it again as well.
		await FocusHiddenInput(first);
		CountHiddenInputFocusCalls();

		finger.Press(SUT.GetAbsoluteBoundsRect().GetCenter());
		finger.Release();
		await WindowHelper.WaitForIdle();

		var (blurs, focuses) = GetHiddenInputFocusCalls();
		Assert.AreEqual(FocusState.Pointer, SUT.FocusState);
		Assert.AreEqual(0, blurs);
		Assert.IsTrue(focuses > 0, "The tap should focus the hidden input again.");
		Assert.IsTrue(IsHiddenInputFocused());
	}

	// Makes the hidden input's bridge take the browser for an iOS one, or not, and the pointer for a finger as a
	// tap on the page would have it: injected input raises no DOM event.
	private static IDisposable SimulateIOS(bool isIOS)
	{
		InvokeBrowserJs($$"""
			(function() {
				const bridge = {{HiddenInputBridge}};
				bridge.detach();
				window.__unoBridge ??= { isIOS: bridge.isIOS, lastPointerType: bridge.lastPointerType };
				bridge.isIOS = {{(isIOS ? "true" : "false")}};
				bridge.lastPointerType = 'touch';
				return '';
			})()
			""");
		return Disposable.Create(() => InvokeBrowserJs($"(function(){{ const bridge = {HiddenInputBridge}; bridge.detach(); Object.assign(bridge, window.__unoBridge); return ''; }})()"));
	}

	// Counts the calls that blur or focus the hidden input from here on. Calls rather than events: a tab in the
	// background raises no focus events.
	private static void CountHiddenInputFocusCalls()
		=> InvokeBrowserJs($$"""
			(function() {
				const input = {{HiddenInput}};
				const calls = window.__unoFocusCalls = { blur: 0, focus: 0 };
				const prototype = Object.getPrototypeOf(input);
				input.blur = function() { calls.blur++; prototype.blur.call(input); };
				input.focus = function(options) { calls.focus++; prototype.focus.call(input, options); };
				return '';
			})()
			""");

	private static (int Blurs, int Focuses) GetHiddenInputFocusCalls()
	{
		var calls = InvokeBrowserJs("(function(){ return window.__unoFocusCalls.blur + ',' + window.__unoFocusCalls.focus; })()").Split(',');
		return (int.Parse(calls[0]), int.Parse(calls[1]));
	}

	private static bool IsHiddenInputFocused()
		=> InvokeBrowserJs($"(function(){{ return document.activeElement === {HiddenInput} ? '1' : '0'; }})()") == "1";

	private static void DisarmTrailingClickGuard()
		=> InvokeBrowserJs($"(function(){{ {HiddenInputBridge}.swallowNextCanvasClick = false; return ''; }})()");

	private static bool IsTrailingClickGuardArmed()
		=> InvokeBrowserJs($"(function(){{ return {HiddenInputBridge}.swallowNextCanvasClick === true ? '1' : '0'; }})()") == "1";
}
#endif
