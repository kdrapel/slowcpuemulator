using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;

namespace System.Windows.Forms {
	/// <summary>
	/// A C# API Layer for handling global hotkeys.
	/// </summary>
	public static class HotkeyListener {
		[SuppressUnmanagedCodeSecurity]
		[DllImport("user32", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

		[SuppressUnmanagedCodeSecurity]
		[DllImport("user32", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

		private delegate bool RegisterHotKeyDelegate(IntPtr hwnd, int id, uint modifiers, uint key);
		private delegate bool UnregisterHotKeyDelegate(IntPtr hwnd, int id);

		private static readonly Delegate Register = new RegisterHotKeyDelegate(RegisterHotKey), Unregister = new UnregisterHotKeyDelegate(UnregisterHotKey);
		private static readonly Dictionary<uint, Hotkey> Hotkeys = new Dictionary<uint, Hotkey>();
		private static readonly object SyncRoot = new object();
		private static bool enabled, firstTime = true;
		private static ManualResetEventSlim resetEvent = new ManualResetEventSlim();
		private static EventHandler idle = Application_Idle;
		private static IntPtr handle;
		private static MessageWindow window;
		private static int ID = 0;
		/// <summary>
		/// Fired when the registered hotkey
		/// </summary>
		public static event Action<Hotkey> HotkeyPressed;

		/// <summary>
		/// Gets or sets whether the hotkey listener is enabled.
		/// </summary>
		public static bool Enabled {
			get {
				return enabled;
			}
			set {
				if (value == enabled)
					return;
				enabled = value;
				if (value) {
					if (window == null) {
						try {
							Thread messageLoopThread = new Thread(InitWindow);
							messageLoopThread.Name = "HotkeyListenerThread";
							messageLoopThread.IsBackground = true;
							messageLoopThread.Start();
							if (resetEvent != null) {
								resetEvent.Wait();
								resetEvent = null;
							}
						} catch {
						}
					}
					if (firstTime) {
						Application.ApplicationExit += Application_ApplicationExit;
						firstTime = false;
					}
				}
			}
		}

		private static void InitWindow() {
			try {
				window = new MessageWindow();
			} catch {
			}
			Application.Idle += idle;
			try {
				Application.Run(window);
			} catch {
			}
		}

		private static void Application_Idle(object sender, EventArgs e) {
			Application.Idle -= idle;
			idle = null;
			try {
				resetEvent.Set();
			} catch {
			}
		}

		private static void Application_ApplicationExit(object sender, EventArgs e) {
			enabled = false;
			if (window == null)
				return;
			lock (SyncRoot) {
				try {
					foreach (Hotkey hotkey in Hotkeys.Values)
						window.Invoke(Unregister, handle, hotkey.id);
				} catch {
				}
				Hotkeys.Clear();
			}
		}

		/// <summary>
		/// Registers the specified hotkey.
		/// </summary>
		/// <param name="hotkey">The hotkey to register.</param>
		public static void RegisterHotKey(Hotkey hotkey) {
			uint hash = hotkey.TrueHash;
			int id;
			lock (SyncRoot) {
				if (Hotkeys.ContainsKey(hash))
					return;
				id = ++ID;
				if (window != null) {
					try {
						if (!((bool) window.Invoke(Register, handle, id, (uint) hotkey.Modifiers, (uint) hotkey.Key))) {
							if (hotkey.Modifiers.HasFlag(KeyModifiers.NoRepeat)) {
								if (!((bool) window.Invoke(Register, handle, id, (uint) (hotkey.Modifiers & ~KeyModifiers.NoRepeat), (uint) hotkey.Key)))
									return;
							} else return;
						}
					} catch {
						return;
					}
				}
				hotkey.id = id;
				Hotkeys.Add(hash, hotkey);
			}
		}

		/// <summary>
		/// Unregisters the specified hotkey.
		/// </summary>
		/// <param name="hotkey">The hotkey to unregister.</param>
		public static void UnregisterHotKey(Hotkey hotkey) {
			uint hash = hotkey.TrueHash;
			if (hotkey.id == 0) {
				lock (SyncRoot) {
					if (Hotkeys.ContainsKey(hash))
						hotkey.id = Hotkeys[hash].id;
					else
						return;
				}
			}
			if (hotkey.id != 0) {
				if (window != null) {
					try {
						window.Invoke(Unregister, handle, hotkey.id);
					} catch {
					}
				}
				hotkey.id = 0;
			}
			lock (SyncRoot) {
				if (Hotkeys.ContainsKey(hash))
					Hotkeys.Remove(hash);
			}
		}

		private class MessageWindow : Form {
			public MessageWindow() {
				CheckForIllegalCrossThreadCalls = false;
				handle = Handle;
			}

			protected override void WndProc(ref Message m) {
				if (m.Msg == 0x312 && enabled) {
					uint trueHash = (uint) m.LParam.ToInt64();
					if (HotkeyPressed != null)
						HotkeyPressed(Hotkeys.ContainsKey(trueHash) ? Hotkeys[trueHash] : new Hotkey(trueHash));
				}
				base.WndProc(ref m);
			}

			protected override void SetVisibleCore(bool value) {
				base.SetVisibleCore(false);
			}

			protected override void OnClosing(CancelEventArgs e) {
				Application_ApplicationExit(this, EventArgs.Empty);
				base.OnClosing(e);
			}

			protected override void OnHandleDestroyed(EventArgs e) {
				base.OnHandleDestroyed(e);
				window = null;
			}
		}
	}
}