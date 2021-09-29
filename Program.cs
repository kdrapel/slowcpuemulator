using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace SlowCpuEmulator {
	public static class Program {
		private const string ParameterHelp = "Argument parameters format:\nslowcpuemulator ExecutableFileToRun.exe optionalParameters interval speedPercentage {hide} {nohotkey} {startdisabled} {adaptive/vanilla/aligned}";
		private const int HWND_TOPMOST = -1, SW_HIDE = 0, SW_SHOW = 5, VK_RETURN = 0x0D, WM_KEYDOWN = 0x100;
		private const double DefaultInterval = 23.0, MinimumInterval = 3.0;
		private const uint TOKEN_READ = 131080u;
		private delegate bool ConsoleCloseEventHandler(CtrlType sig);
		private static event ConsoleCloseEventHandler ConsoleClose;
		private static readonly object SyncRoot = new object();
		private static readonly Comparison<Process> compareProcesses = CompareProcesses;
		private static readonly Func<bool> isTimeForSuspension = IsTimeForSuspension, isSuspensionCompleted = IsSuspensionCompleted;
		private static readonly char[] curlyBrackets = new char[] { '{', '}' };
		private static readonly Process currentProcess;
		private static readonly string commandLine, commandLineLower, executablePath, executableName;
		private static readonly ManualResetEventSlim processSelectedEvent;
		private static readonly Hotkey stateHotkey = new Hotkey(Keys.Scroll, KeyModifiers.Shift | KeyModifiers.NoRepeat);
		private static readonly Hotkey percentageUpHotkey = new Hotkey(Keys.F9, KeyModifiers.Shift | KeyModifiers.NoRepeat);
		private static readonly Hotkey percentageDownHotkey = new Hotkey(Keys.F8, KeyModifiers.Shift | KeyModifiers.NoRepeat);
		private static readonly Hotkey intervalUpHotkey = new Hotkey(Keys.F9, KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.NoRepeat);
		private static readonly Hotkey intervalDownHotkey = new Hotkey(Keys.F8, KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.NoRepeat);
		private static readonly Hotkey showHotkey = new Hotkey(Keys.Scroll, KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.NoRepeat);
		private static readonly Hotkey modeHotkey = new Hotkey(Keys.Scroll, KeyModifiers.Alt | KeyModifiers.NoRepeat);
		private static readonly PreciseStopwatch suspendStopwatch = new PreciseStopwatch(), resumeStopwatch = new PreciseStopwatch();
		private static bool askingForProcess, isGui, suspended, firstClear = true, firstLoad = true, welcome = true, hotkeysEnabled = true, running = true, firstWarning = true, firstInterval = true, firstSpeed = true, isShown = true;
		private static readonly string HotkeyHelp =
@"Hotkeys:
Toggle emulation: " + stateHotkey.ToString() + @"
Increase speed by 3%: " + percentageUpHotkey.ToString() + @"
Decrease speed by 3%: " + percentageDownHotkey.ToString() + @"
Increase interval by 2ms: " + intervalUpHotkey.ToString() + @"
Decrease interval by 2ms: " + intervalDownHotkey.ToString() + @"
Toggle show/hide window: " + showHotkey.ToString() + @"
Switch timer mode: " + modeHotkey.ToString();
		private static EmulationMode Mode = EmulationMode.Adaptive;
		private static double interval = DefaultInterval, suspendedTime, inputVal = 100.0;
		private static int lineLength, intervalUpdateCounter, skipLines;
		private static IntPtr consoleHandle;
		private static Process process;

		static Program() {
			string exePath;
			try {
				exePath = Assembly.GetEntryAssembly().Location;
			} catch {
				exePath = "";
			}
			executablePath = exePath.ToLower();
			try {
				executableName = Path.GetFileNameWithoutExtension(executablePath);
			} catch {
				executableName = "";
			}
			try {
				currentProcess = Process.GetCurrentProcess();
			} catch {
			}
			try {
				commandLine = Environment.CommandLine.Trim().Replace("\"", "");
			} catch {
				commandLine = "";
			}
			commandLineLower = commandLine.ToLower();
			try {
				FileUtils.Directories.Add(Path.GetDirectoryName(exePath));
			} catch {
			}
			try {
				FileUtils.Directories.Add(Environment.CurrentDirectory);
			} catch {
			}
			try {
				STARTUPINFO info;
				GetStartupInfo(out info);
				if ((info.dwFlags & 0x00000800) != 0)
					FileUtils.Directories.Add(Path.GetDirectoryName(info.lpTitle));
			} catch {
			}
			try {
				processSelectedEvent = new ManualResetEventSlim();
			} catch {
			}
		}

		public enum EmulationMode {
			Adaptive = 0,
			Vanilla,
			Aligned,
		}

		/*public static bool IsErrorUncaptured {
			get {
				try {
					return IsHandleUncaptured(GetStdHandle(-12));
				} catch {
					return false;
				}
			}
		}*/

		public static bool IsInputUncaptured {
			get {
				try {
					return IsHandleUncaptured(GetStdHandle(-10));
				} catch {
					return false;
				}
			}
		}

		public static bool IsOutputUncaptured {
			get {
				try {
					return IsHandleUncaptured(GetStdHandle(-11));
				} catch {
					return false;
				}
			}
		}

		private static bool IsHandleUncaptured(IntPtr ioHandle) {
			int num;
			return (GetFileType(new SafeFileHandle(ioHandle, false)) & 2) == 2 ? GetConsoleMode(ioHandle, out num) : false;
		}

		[Flags]
		private enum ThreadAccess : int {
			TERMINATE = 0x0001,
			SUSPEND_RESUME = 0x0002,
			GET_CONTEXT = 0x0008,
			SET_CONTEXT = 0x0010,
			SET_INFORMATION = 0x0020,
			QUERY_INFORMATION = 0x0040,
			SET_THREAD_TOKEN = 0x0080,
			IMPERSONATE = 0x0100,
			DIRECT_IMPERSONATION = 0x0200
		}

		private enum CtrlType {
			CTRL_C_EVENT = 0,
			CTRL_BREAK_EVENT = 1,
			CTRL_CLOSE_EVENT = 2,
			CTRL_LOGOFF_EVENT = 5,
			CTRL_SHUTDOWN_EVENT = 6
		}

		private static bool IsElevated {
			get {
				try {
					return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
				} catch {
					return false;
				}
			}
		}

		private static bool HasExited {
			get {
				try {
					return process != null && process.HasExited;
				} catch {
					ShowWarning();
					return false;
				}
			}
		}

		[STAThread]
		public static void Main() {
			int firstIndex;
			List<string> messages = new List<string>();
			if (firstLoad) {
				firstLoad = false;
				try {
					consoleHandle = currentProcess.MainWindowHandle;
				} catch {
				}
				firstIndex = commandLineLower.IndexOf(executableName);
				try {
					currentProcess.PriorityClass = ProcessPriorityClass.High;
				} catch {
				}
				try {
					currentProcess.EnableRaisingEvents = true;
				} catch {
				}
				currentProcess.Exited += CurrentProcess_Exited;
				ConsoleClose += ConsoleCloseEvent;
				try {
					SetConsoleCtrlHandler(ConsoleClose, true);
				} catch {
				}
				try {
					isGui = IsOutputUncaptured && IsInputUncaptured && Environment.UserInteractive && Console.CursorTop == 0 && Console.CursorLeft == 0;
				} catch {
					isGui = false;
				}
				if (isGui) {
					if (firstClear) {
						firstClear = false;
						try {
							Console.Title = "Slow Cpu Emulator v" + Application.ProductVersion + " (developed by MathuSum Mut)";
						} catch {
						}
						try {
							Console.BackgroundColor = ConsoleColor.DarkBlue;
						} catch {
						}
						try {
							Console.ForegroundColor = ConsoleColor.White;
						} catch {
						}
						try {
							Console.Clear();
						} catch {
						}
					}
				} else {
					try {
						if (!Console.Title.Contains("(developed by MathuSum Mut)"))
							Console.Title += " (developed by MathuSum Mut)";
					} catch {
					}
					if (welcome)
						welcome = false;
					else
						return;
				}
				WriteLine("\nSlow Cpu Emulator and Cpu Usage Reduction tool was developed by MathuSum Mut (2016)\n\nTo find the latest version go to: https://github.com/mathusummut/SlowCpuEmulator\n");
				if (!commandLine.Contains("?"))
					WriteLine("Use '?' as command line parameter to get help and documentation for supported parameters.\n");
				try {
					Process[] processes = Process.GetProcesses();
					int count = 0;
					foreach (Process process in processes) {
						try {
							if (executablePath == process.MainModule.FileName.ToLower())
								count++;
						} catch {
						}
					}
					if (count > 10) {
						WriteLine("A maximum of 10 instances of SlowCpuEmulator are allowed in order to reduce inconsistencies.");
						if (isGui) {
							try {
								Thread.Sleep(1500);
							} catch {
							}
						}
						return;
					}
				} catch {
				}
				try {
					Thread processChecker = new Thread(ProcessChecker);
					processChecker.IsBackground = true;
					processChecker.Priority = ThreadPriority.Lowest;
					processChecker.Name = "ProcessChecker";
					processChecker.Start();
				} catch {
				}
			} else
				firstIndex = -1;
		restart:
			if (firstIndex == -1) {
				if (messages.Count != 0) {
					foreach (string message in messages)
						WriteLine(message);
				}
				Prompt();
			} else {
				firstIndex += executableName.Length;
				int before;
				for (; firstIndex < commandLineLower.Length; firstIndex++) {
					switch (commandLine[firstIndex]) {
						case ' ':
							break;
						case '\\':
							before = firstIndex;
							firstIndex = commandLineLower.IndexOf(executableName, firstIndex);
							if (firstIndex == -1) {
								firstIndex = before;
								break;
							} else {
								firstIndex += executableName.Length;
								continue;
							}
						default:
							continue;
					}
					break;
				}
				if (firstIndex >= commandLine.Length - 1)
					Prompt();
				else {
					string args = commandLine.Substring(firstIndex + 1).Trim();
					if (args.Length != 0 && (args[0] == '?' || (args.Length > 1 && args[1] == '?') || (args.Length > 2 && args[2] == '?'))) {
						WriteLine(
@"SlowCpuEmulator is useful for slowing down games, testing how applications deal with lag, and/or reducing the overall CPU usage of a specified process.

" + HotkeyHelp + @"

" + ParameterHelp + @"

Example 1: slowcpuemulator.exe -i
Opens file dialog to choose executable or shortcut to open.

Example 2: slowcpuemulator.exe Game 1.exe 22 35
Runs Game 1.exe slowed down with interval 22ms at 35% speed emulation.

Example 3: slowcpuemulator C:\New Folder\Game 1.exe 22 35% hide nohotkey
Like example 2 but with full path, does not show the console window and disables hotkeys.

Example 4: slowcpuemulator Game 1.exe -windowed 22ms 35% nohotkey
Like example 2 but passes a '-windowed' argument parameter to the target process, does not hide window, but disables hotkeys.

Example 5: slowcpuemulator Game 1 -windowed 22ms 35% vanilla
Similar to example 4 but THIS DOES NOT WORK. Passing parameters to an executable and having a space in the executable name that omits the .exe extension are mutually exclusive. To resolve this be sure to add a '.exe' extension to the file name like in example 4.

Example 6: slowcpuemulator ..\Game 2.exe ? 22 35 startdisabled hide vanilla
Opens 'Game 2.exe' from the parent directory with the specified parameters, hides the window, starts the emulator disabled and sets the timing mode to vanilla.

Example 7: slowcpuemulator slowcpuemulator ? 22 35
This causes slowcpuemulator to open a new instance of slowcpuemulator that shows its help dialog slowed down to a 22ms interval at 35% ratio. Pretty cool eh? ;)

Quotes and curly brackets are entirely optional. Forward slashes can be used instead of backslashes.");
						if (isGui) {
							WriteLine("\nPress any key to close...");
							try {
								Console.ReadKey(true);
							} catch {
							}
						}
						return;
					} else if (args.Equals("i", StringComparison.InvariantCultureIgnoreCase) || args.Equals("-i", StringComparison.InvariantCultureIgnoreCase) || args.Equals("--i", StringComparison.InvariantCultureIgnoreCase)) {
						try {
							using (OpenFileDialog dialog = new OpenFileDialog()) {
								dialog.AddExtension = true;
								dialog.CheckFileExists = true;
								dialog.CheckPathExists = true;
								dialog.DefaultExt = "exe";
								dialog.Filter = "Executable|*.exe|Shortcut|*.lnk";
								try {
									dialog.DereferenceLinks = false;
								} catch {
								}
								try {
									dialog.Title = "Select executable or shortcut...";
								} catch {
								}
								WriteLine("Select executable or shortcut...");
								if (dialog.ShowDialog() == DialogResult.OK) {
									string pathFileName = dialog.FileName.ToLower();
									int count = 0;
									try {
										Process[] processes = Process.GetProcesses();
										foreach (Process process in processes) {
											try {
												if (pathFileName == process.MainModule.FileName.ToLower() && pathFileName == executablePath)
													count++;
											} catch {
											}
										}
									} catch {
									}
									string processMessage = "";
									if (count > 9)
										processMessage = "A maximum of 10 instances of SlowCpuEmulator are allowed in order to reduce inconsistencies.";
									else {
										try {
											process = Process.Start(dialog.FileName);
											processMessage = "";
											try {
												processSelectedEvent.Set();
											} catch {
											}
										} catch {
											if (processMessage.Length == 0)
												processMessage = "Could not start the executable \"" + dialog.FileName + "\".";
										}
									}
									if (processMessage.Length == 0) {
										WriteLine(dialog.FileName + " successfully started with process ID " + process.Id + ".");
										EnterInterval();
									} else if (isShown) {
										WriteLine(processMessage);
										EnterProcessID();
										EnterInterval();
									} else {
										Environment.Exit(0);
										return;
									}
								} else {
									WriteLine();
									firstIndex = -1;
									goto restart;
								}
							}
						} catch {
							firstIndex = -1;
							goto restart;
						}
					} else {
						int paramIndex = args.Length - 1;
						int oldParamIndex = paramIndex + 1;
						double temp;
						string parameter = "";
						try {
							int bracketState = 0;
							do {
								for (; paramIndex >= 0; paramIndex--) {
									switch (args[paramIndex]) {
										case ' ':
											if (bracketState == 0) {
												paramIndex++;
												break;
											} else
												continue;
										case '}':
											bracketState++;
											continue;
										case '{':
											bracketState--;
											if (bracketState < 0)
												break;
											continue;
										default:
											continue;
									}
									break;
								}
								if (bracketState != 0) {
									messages.Add("Curly brackets were mismatched.");
									firstIndex = -1;
									goto restart;
								}
								parameter = args.Substring(paramIndex, oldParamIndex - paramIndex);
								if (double.TryParse(TrimCustom(parameter), out temp))
									break;
								if (paramIndex <= 0) {
									messages.Add("Invalid parameters were given.");
									firstIndex = -1;
									goto restart;
								}
								oldParamIndex = paramIndex;
								paramIndex -= 2;
							} while (true);
							if (oldParamIndex < args.Length) {
								parameter = args.Substring(oldParamIndex).Trim();
								args = args.Substring(0, oldParamIndex).Trim();
							}
							List<string> parameters;
							try {
								parameters = SplitParams(parameter);
							} catch {
								messages.Add("Curly brackets were mismatched.");
								firstIndex = -1;
								goto restart;
							}
							string param;
							for (int i = 0; i < parameters.Count; i++) {
								param = parameters[i] + " ";
								if (param.StartsWith("hide ")) {
									if (isGui && isShown) {
										try {
											ShowWindow(consoleHandle, SW_HIDE);
											isShown = false;
										} catch {
										}
									}
								} else if (param.StartsWith("nohotkey"))
									hotkeysEnabled = false;
								else if (param.StartsWith("startdisabled")) {
									if (hotkeysEnabled)
										running = false;
									else {
										WriteLine("Cannot enable emulation if hotkeys are disabled and initial state is disabled.");
										if (isGui && isShown) {
											WriteLine("\nPress any key to close...");
											try {
												Console.ReadKey(true);
											} catch {
											}
										}
										return;
									}
								} else if (param.StartsWith("adaptive"))
									Mode = EmulationMode.Adaptive;
								else if (param.StartsWith("vanilla"))
									Mode = EmulationMode.Vanilla;
								else if (param.StartsWith("aligned"))
									Mode = EmulationMode.Aligned;
							}
						} catch {
						}
						int percentageIndex = args.LastIndexOf(' ') + 1;
						if (percentageIndex <= 1) {
							messages.Add(ParameterHelp + "\n");
							firstIndex = -1;
							goto restart;
						}
						int intervalIndex = args.LastIndexOf(' ', percentageIndex - 2) + 1;
						if (intervalIndex <= 1) {
							messages.Add(ParameterHelp + "\n");
							firstIndex = -1;
							goto restart;
						}
						string path = args.Substring(0, intervalIndex).Trim();
						if (path.Length == 0) {
							messages.Add(ParameterHelp + "\n");
							firstIndex = -1;
							goto restart;
						}
						string intervalMessage = "", inputValMessage = "", processMessage = "";
						try {
							interval = double.Parse(TrimCustom(args.Substring(intervalIndex, (percentageIndex - intervalIndex) - 1)));
							if (interval < MinimumInterval)
								intervalMessage = "An interval smaller than " + MinimumInterval + "ms is not recommended.";
							intervalUpdateCounter = 2;
						} catch {
							intervalMessage = "The specified interval is invalid.";
						}
						try {
							inputVal = double.Parse(TrimCustom(args.Substring(percentageIndex)));
							if (inputVal < 0.0 || inputVal > 100.0)
								inputValMessage = "Invalid percentage.";
							else
								suspendedTime = interval - inputVal * 0.01 * interval;
						} catch {
							inputValMessage = "The specified speed percentage is invalid.";
						}
						args = "";
						try {
							path = path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
							string oldPath = path;
							int startIndex = path.LastIndexOf('\\') + 1;
							int index = -1;
							int oldIndex = index;
							do {
								index = path.IndexOf(".exe", startIndex);
								if (index == -1) {
									index = oldIndex;
									break;
								} else if (path.Length <= index + 5 || path[index + 4] == ' ')
									break;
								else {
									startIndex = index + 5;
									oldIndex = index;
								}
							} while (true);
							oldIndex = index;
							do {
								index = path.IndexOf(".lnk", startIndex);
								if (index == -1) {
									index = oldIndex;
									break;
								} else if (path.Length <= index + 5 || path[index + 4] == ' ')
									break;
								else {
									startIndex = index + 5;
									oldIndex = index;
								}
							} while (true);
							if (index == -1) {
								oldPath += ".exe";
								index = path.IndexOf(' ', startIndex);
								if (index == -1)
									path = path.Trim() + ".exe";
								else {
									args = path.Substring(index).Trim();
									path = path.Substring(0, index).Trim() + ".exe";
								}
							} else {
								index += 4;
								args = path.Substring(index).Trim();
								path = path.Substring(0, index).Trim();
							}
							string resolvedPath = FileUtils.ResolvePath(path);
							if (resolvedPath == null) {
								resolvedPath = FileUtils.ResolvePath(oldPath);
								if (resolvedPath == null) {
									resolvedPath = FileUtils.ResolvePath(path.Substring(0, path.Length - 4) + ".lnk");
									if (resolvedPath == null) {
										resolvedPath = FileUtils.ResolvePath(oldPath.Substring(0, oldPath.Length - 4) + ".lnk");
										if (resolvedPath == null)
											processMessage = "\"" + path + "\" was not found.";
										else {
											path = resolvedPath;
											args = "";
										}
									} else
										path = resolvedPath;
								} else {
									path = resolvedPath;
									args = "";
								}
							} else
								path = resolvedPath;
							string pathFileName;
							int count = 0;
							try {
								if (path.EndsWith(".lnk"))
									pathFileName = Path.GetFullPath(FileUtils.ResolveShortcut(path));
								else
									pathFileName = Path.GetFullPath(path);
								pathFileName = pathFileName.ToLower();
								Process[] processes = Process.GetProcesses();
								foreach (Process process in processes) {
									try {
										if (pathFileName == process.MainModule.FileName.ToLower() && pathFileName == executablePath)
											count++;
									} catch {
									}
								}
							} catch {
							}
							if (count > 9)
								processMessage = "A maximum of 10 instances of SlowCpuEmulator are allowed in order to reduce inconsistencies.";
							else {
								if (args.Length == 0)
									process = Process.Start(path);
								else
									process = Process.Start(path, args);
								processMessage = "";
								try {
									processSelectedEvent.Set();
								} catch {
								}
							}
						} catch {
							if (processMessage.Length == 0) {
								if (args.Length == 0)
									processMessage = "Could not start the executable \"" + path + "\".";
								else
									processMessage = "Could not start the executable \"" + path + "\" with arguments \"" + args + "\".";
							}
						}
						bool skipLine = false;
						if (processMessage.Length == 0) {
							if (intervalMessage.Length != 0) {
								WriteLine(intervalMessage);
								skipLine = true;
								if (isShown)
									EnterInterval();
								else {
									Environment.Exit(0);
									return;
								}
							}
							if (inputValMessage.Length != 0) {
								if (skipLine)
									WriteLine();
								WriteLine(inputValMessage);
								skipLine = true;
							}
							if (!skipLine)
								WriteLine(path + " successfully started with process ID " + process.Id + " with refresh interval " + interval + "ms and speed percentage " + inputVal + "%.");
						} else {
							if (intervalMessage.Length != 0) {
								WriteLine(intervalMessage);
								skipLine = true;
							}
							if (inputValMessage.Length != 0) {
								if (skipLine)
									WriteLine();
								WriteLine(inputValMessage);
								skipLine = true;
							}
							if (!skipLine)
								WriteLine(processMessage);
							if (isShown) {
								EnterProcessID();
								EnterInterval();
							} else {
								Environment.Exit(0);
								return;
							}
						}
						firstInterval = false;
						firstSpeed = false;
					}
				}
			}
			try {
				Thread thread = new Thread(EmulateSlowCpu);
				thread.Priority = ThreadPriority.Highest;
				thread.Name = "EmulationThread";
				thread.IsBackground = true;
				thread.Start();
			} catch {
				WriteLine("An error occurred while starting the emulation thread.");
				if (isGui && isShown) {
					WriteLine("\nPress any key to close...");
					try {
						Console.ReadKey(true);
					} catch {
					}
				}
				return;
			}
			if (hotkeysEnabled) {
				HotkeyListener.Enabled = true;
				HotkeyListener.HotkeyPressed += HotkeyListener_HotkeyPressed;
				HotkeyListener.RegisterHotKey(stateHotkey);
				HotkeyListener.RegisterHotKey(percentageUpHotkey);
				HotkeyListener.RegisterHotKey(percentageDownHotkey);
				HotkeyListener.RegisterHotKey(intervalUpHotkey);
				HotkeyListener.RegisterHotKey(intervalDownHotkey);
				HotkeyListener.RegisterHotKey(showHotkey);
				HotkeyListener.RegisterHotKey(modeHotkey);
				WriteLine();
				WriteLine(HotkeyHelp);
			}
			string inputValStr;
			while (!HasExited) {
				do {
					if (firstSpeed) {
						firstSpeed = false;
						Write("\nChoose the speed percentage you want the process to run at from 0% to 100%, where 0% means the process is suspended and 100% means the process runs at the normal speed.\n\nType in the speed percentage (0% to 100%, or type -1 to replace interval): ");
					} else
						Write("\nEnter a new speed percentage (0% to 100%, or type -1 to replace interval): ");
					try {
						inputValStr = ReadLine();
						if (HasExited) {
							if (isShown) {
								firstIndex = -1;
								goto restart;
							} else {
								Environment.Exit(0);
								return;
							}
						}
						inputVal = double.Parse(TrimCustom(inputValStr));
						if (inputVal < 0.0) {
							EnterInterval();
							continue;
						} else if (inputVal > 100.0)
							inputVal = 100.0;
						suspendedTime = interval - inputVal * 0.01 * interval;
					} catch {
						WriteLine("Invalid percentage.");
						continue;
					}
					break;
				} while (true);
				WriteLine("Applied.");
			}
			ResumeProcess(process);
			Environment.Exit(0);
		}

		private static void ProcessChecker() {
			do {
				try {
					processSelectedEvent.Wait();
					processSelectedEvent.Reset();
				} catch {
				}
				while (!HasExited) {
					try {
						Thread.Sleep(200);
					} catch {
					}
				}
				if (isShown) {
					if (!isGui || askingForProcess) {
						WriteStatusMessage("The target process has terminated, press Enter to continue...");
						WriteLine();
					} else {
						WriteLine("\nThe target process has terminated.");
						try {
							PostMessage(consoleHandle, WM_KEYDOWN, VK_RETURN, 0);
						} catch {
						}
					}
				} else {
					Environment.Exit(0);
					return;
				}
			} while (true);
		}

		private static List<string> SplitParams(string parameterString) {
			int bracketState = 0;
			int oldParamIndex = 0;
			List<string> parameters = new List<string>();
			string candidate;
			int paramIndex;
			for (paramIndex = 0; paramIndex < parameterString.Length; paramIndex++) {
				switch (parameterString[paramIndex]) {
					case ' ':
						if (bracketState == 0) {
							candidate = parameterString.Substring(oldParamIndex, paramIndex - oldParamIndex).Trim();
							if (candidate.Length != 0)
								parameters.Add(candidate);
							oldParamIndex = paramIndex + 1;
						}
						continue;
					case '}':
						bracketState--;
						if (bracketState == 0) {
							candidate = parameterString.Substring(oldParamIndex, paramIndex - oldParamIndex).Trim();
							if (candidate.Length != 0)
								parameters.Add(candidate);
							oldParamIndex = paramIndex + 1;
							continue;
						} else if (bracketState < 0)
							break;
						else
							continue;
					case '{':
						if (bracketState == 0)
							oldParamIndex = paramIndex + 1;
						bracketState++;
						continue;
					default:
						continue;
				}
				break;
			}
			if (bracketState == 0) {
				candidate = parameterString.Substring(oldParamIndex, paramIndex - oldParamIndex).Trim();
				if (candidate.Length != 0)
					parameters.Add(candidate);
				return parameters;
			} else
				throw new FormatException("Curly brackets were mismatched.");
		}

		private static void HotkeyListener_HotkeyPressed(Hotkey keys) {
			if (keys == stateHotkey) {
				running = !running;
				intervalUpdateCounter = 2;
				if (running)
					WriteStatusMessage("Emulation is now enabled.");
				else {
					ResumeProcess(process);
					WriteStatusMessage("Emulation is now disabled.");
				}
			} else if (keys == percentageUpHotkey) {
				inputVal = Math.Min(inputVal + 3.0, 100.0);
				suspendedTime = interval - inputVal * 0.01 * interval;
				WriteStatusMessage("Speed percentage is now " + inputVal + "%.");
			} else if (keys == percentageDownHotkey) {
				inputVal = Math.Max(inputVal - 3.0, 0.0);
				suspendedTime = interval - inputVal * 0.01 * interval;
				WriteStatusMessage("Speed percentage is now " + inputVal + "%.");
			} else if (keys == intervalUpHotkey) {
				interval = Math.Max(interval + 2.0, 0.0);
				intervalUpdateCounter = 2;
				WriteStatusMessage("Update interval is now " + interval + "ms.");
			} else if (keys == intervalDownHotkey) {
				interval = Math.Max(interval - 2.0, 0.0);
				intervalUpdateCounter = 2;
				WriteStatusMessage("Update interval is now " + interval + "ms.");
			} else if (keys == showHotkey) {
				if (isGui) {
					try {
						ShowWindow(consoleHandle, isShown ? SW_HIDE : SW_SHOW);
						isShown = !isShown;
					} catch {
					}
				}
			} else if (keys == modeHotkey) {
				Mode = (EmulationMode) (((int) Mode + 1) % 3);
				WriteStatusMessage("Timing mode in now set to: " + Mode.ToString() + (Mode == EmulationMode.Adaptive ? " (default)." : "."));
			}
		}

		private static void Prompt() {
			if (isShown) {
				EnterProcessID(false);
				EnterInterval();
			} else
				Environment.Exit(0);
		}

		private static void CurrentProcess_Exited(object sender, EventArgs e) {
			ResumeProcess(process);
		}

		private static int CompareProcesses(Process left, Process right) {
			try {
				return left.ProcessName.CompareTo(right.ProcessName);
			} catch {
				return 0;
			}
		}

		private static void EnterProcessID(bool skip = true) {
		begin:
			if (skip) {
				WriteLine();
				skip = false;
			}
			Process[] processes = null;
			bool gotProcesses = false;
			try {
				processes = Process.GetProcesses();
				gotProcesses = true;
			} catch {
			}
			if (gotProcesses) {
				WriteLine("Process List (PID & Name): \n");
				Array.Sort(processes, compareProcesses);
				string description;
				bool written, processNameWritten;
				foreach (Process process in processes) {
					try {
						Write(process.Id.ToString());
						written = true;
					} catch {
						written = false;
					}
					try {
						if (written)
							Write(": " + process.ProcessName);
						else
							Write(process.ProcessName);
						processNameWritten = true;
					} catch {
						processNameWritten = false;
					}
					try {
						description = process.MainModule.FileVersionInfo.FileDescription.Trim();
						if (description.Length != 0) {
							if (processNameWritten)
								Write(" (" + description + ")");
							else if (written)
								Write(": " + description);
							else
								Write(description);
						}
					} catch {
					}
					WriteLine();
				}
				WriteLine("\nProcess Count: " + processes.Length);
				Write("\nEnter the ID of the process to slow down (or press enter to refresh process list, or type -1 to exit): ");
			} else
				Write("Enter the ID of the process to slow down (or press enter to refresh process list, or type -1 to exit): ");
			string input = "";
			Process oldProcess = process;
			askingForProcess = true;
			do {
				try {
					input = TrimCustom(ReadLine());
					if (input.Length == 0) {
						if (HasExited && !isShown) {
							Environment.Exit(0);
							return;
						} else {
							WriteLine();
							goto begin;
						}
					}
					process = Process.GetProcessById(int.Parse(input));
				} catch {
					if (input == "-1") {
						if (oldProcess != null)
							ResumeProcess(oldProcess);
						Environment.Exit(0);
						return;
					} else {
						Write("Invalid process ID. Please enter a valid PID (or press enter to refresh process list, or type -1 to exit): ");
						continue;
					}
				}
				if (process.Id == currentProcess.Id)
					Write("Cheeky m8 ;) Kindly choose the ID of another process (or press enter to refresh process list, or type -1 to exit): ");
				else
					break;
			} while (true);
			askingForProcess = false;
			if (oldProcess != null)
				ResumeProcess(oldProcess);
			try {
				processSelectedEvent.Set();
			} catch {
			}
			try {
				Thread.Sleep(0);
			} catch {
			}
			firstWarning = true;
		}

		private static void EnterInterval() {
			if (firstInterval) {
				Write("\nThis is the tricky part. Choose the refresh interval in milliseconds, where smaller values yield less stutter and smoother slowdown, but will be less accurate when it comes to speed consistency, and larger values mean more speed consistency and accuracy, but can introduce stutter. You need to find the sweet spot for your CPU. For my i7 4790 CPU, " + DefaultInterval + "ms is the sweet spot.\n\nValues smaller than " + MinimumInterval + "ms are not recommended.\n");
				firstInterval = false;
			}
		loop:
			Write("\nType in the desired interval (default: " + DefaultInterval + "ms, type '-1' to change PID): ");
			string temp;
			do {
				try {
					temp = TrimCustom(ReadLine());
					if (HasExited) {
						if (isShown)
							Main();
						else {
							Environment.Exit(0);
							return;
						}
					}
					interval = temp.Length == 0 ? DefaultInterval : double.Parse(temp);
					if (interval <= 0.01) {
						EnterProcessID();
						goto loop;
					}
					intervalUpdateCounter = 2;
				} catch {
					Write("Invalid interval. Please enter a proper interval (or type '-1' to change PID): ");
					continue;
				}
				if (interval < MinimumInterval)
					Write("An interval smaller than " + MinimumInterval + "ms is not recommended. Please enter a higher interval or type '-1' to change PID: ");
				else
					break;
			} while (true);
			suspendedTime = interval - inputVal * 0.01 * interval;
		}

		private static void ShowWarning() {
			if (firstWarning) {
				firstWarning = false;
				try {
					Thread.Sleep(100);
				} catch {
				}
				if (IsElevated)
					WriteStatusMessage("Warning: Some trouble was encountered while trying to slow down the specified process.");
				else
					WriteStatusMessage("Warning: Some trouble was encountered while trying to slow down the specified process. Try running the application as administrator.");
			}
		}

		private static void WriteStatusMessage(string str) {
			try {
				Console.SetCursorPosition(lineLength, Console.CursorTop + skipLines);
			} catch {
			}
			skipLines++;
			try {
				Console.Write("\n" + str);
			} catch {
			}
			try {
				Console.SetCursorPosition(lineLength, Console.CursorTop - skipLines);
			} catch {
			}
		}

		private static void Write(string str) {
			if (str == null)
				return;
			int index = str.LastIndexOf('\n');
			if (index == -1)
				lineLength += str.Length;
			else if (index < str.Length - 1)
				lineLength += str.Substring(index + 1).Length;
			while (skipLines > 0) {
				skipLines--;
				try {
					Console.WriteLine();
				} catch {
				}
			}
			try {
				Console.Write(str);
			} catch {
			}
		}

		private static void WriteLine(string str) {
			if (str == null)
				str = "null";
			lineLength = 0;
			while (skipLines > 0) {
				skipLines--;
				try {
					Console.WriteLine();
				} catch {
				}
			}
			try {
				Console.WriteLine(str);
			} catch {
			}
		}

		private static void WriteLine() {
			while (skipLines > 0) {
				skipLines--;
				try {
					Console.WriteLine();
				} catch {
				}
			}
			try {
				Console.WriteLine();
			} catch {
			}
		}

		private static string ReadLine() {
			string line;
			try {
				line = Console.ReadLine();
			} catch {
				line = "";
			}
			lineLength = 0;
			return line;
		}

		private static string TrimCustom(string input) {
			if (input == null || input.Length == 0)
				return "";
			input = input.Trim(curlyBrackets).Trim();
			if (input.Length == 0)
				return "";
			int i = 0;
			for (; i < input.Length && (char.IsDigit(input[i]) || input[i] == '-' || input[i] == '.'); i++)
				;
			return input.Substring(0, i);
		}

		private static void EmulateSlowCpu() {
			suspendStopwatch.ElapsedTicks = 0.0;
			resumeStopwatch.ElapsedTicks = 0.0;
			double elapsedMilliseconds;
			while (!HasExited) {
				if (running && suspendedTime > float.Epsilon) {
					if (Mode == EmulationMode.Vanilla)
						suspendStopwatch.ElapsedTicks = 0.0;
					suspendStopwatch.Running = true;
					SuspendProcess(process);
					try {
						SpinWait.SpinUntil(isSuspensionCompleted);
					} catch {
						while (suspendStopwatch.ElapsedMilliseconds < suspendedTime) {
							try {
								Thread.Sleep(0);
							} catch {
							}
						}
					}
					if (Mode != EmulationMode.Vanilla) {
						if (intervalUpdateCounter == 0) {
							if (Mode == EmulationMode.Adaptive) {
								elapsedMilliseconds = suspendStopwatch.ElapsedMilliseconds;
								if (elapsedMilliseconds < 3.0 * suspendedTime)
									suspendStopwatch.ElapsedMilliseconds -= suspendedTime;
								else
									suspendStopwatch.ElapsedMilliseconds %= suspendedTime;
							} else
								suspendStopwatch.ElapsedMilliseconds %= suspendedTime;
						} else {
							intervalUpdateCounter--;
							suspendStopwatch.ElapsedTicks = 0.0;
						}
						suspendStopwatch.Running = false;
					}
				}
				if (running && interval - suspendedTime > float.Epsilon)
					ResumeProcess(process);
				if (!HasExited) {
					resumeStopwatch.Running = true;
					if (running && interval - suspendedTime > float.Epsilon) {
						try {
							SpinWait.SpinUntil(isTimeForSuspension);
						} catch {
							while (resumeStopwatch.ElapsedMilliseconds < interval) {
								try {
									Thread.Sleep(0);
								} catch {
								}
							}
						}
					} else {
						try {
							Thread.Sleep(100);
						} catch {
						}
					}
					if (Mode == EmulationMode.Vanilla)
						resumeStopwatch.ElapsedTicks = 0.0;
					else {
						if (intervalUpdateCounter == 0) {
							if (Mode == EmulationMode.Adaptive) {
								elapsedMilliseconds = resumeStopwatch.ElapsedMilliseconds;
								if (elapsedMilliseconds < 3.0 * interval)
									resumeStopwatch.ElapsedMilliseconds -= interval;
								else
									resumeStopwatch.ElapsedMilliseconds %= interval;
							} else
								resumeStopwatch.ElapsedMilliseconds %= interval;
						} else {
							intervalUpdateCounter--;
							resumeStopwatch.ElapsedTicks = 0.0;
						}
					}
				}
			}
		}

		private static bool IsSuspensionCompleted() {
			return suspendStopwatch.ElapsedMilliseconds >= suspendedTime;
		}

		private static bool IsTimeForSuspension() {
			return resumeStopwatch.ElapsedMilliseconds >= interval;
		}

		public static void SuspendProcess(Process process) {
			if (process == null || suspended)
				return;
			try {
				if (HasExited || process.ProcessName.Length == 0)
					return;
				lock (SyncRoot) {
					suspended = true;
					IntPtr pOpenThread;
					foreach (ProcessThread pT in process.Threads) {
						try {
							pOpenThread = OpenThread(ThreadAccess.SUSPEND_RESUME, false, (uint) pT.Id);
							if (pOpenThread == IntPtr.Zero)
								continue;
							SuspendThread(pOpenThread);
							CloseHandle(pOpenThread);
						} catch {
						}
					}
				}
			} catch {
				ShowWarning();
			}
		}

		private static void ResumeProcess(Process process) {
			if (process == null || !suspended)
				return;
			try {
				if (HasExited || process.ProcessName.Length == 0)
					return;
				lock (SyncRoot) {
					suspended = false;
					IntPtr pOpenThread;
					foreach (ProcessThread pT in process.Threads) {
						try {
							pOpenThread = OpenThread(ThreadAccess.SUSPEND_RESUME, false, (uint) pT.Id);
							if (pOpenThread == IntPtr.Zero)
								continue;
							while (ResumeThread(pOpenThread) != 0)
								;
							CloseHandle(pOpenThread);
						} catch {
						}
					}
				}
			} catch {
				ShowWarning();
			}
		}

		private static bool ConsoleCloseEvent(CtrlType sig) {
			ResumeProcess(process);
			return false;
		}

		[DllImport("user32.dll")]
		private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

		[DllImport("kernel32")]
		private static extern bool SetConsoleCtrlHandler(ConsoleCloseEventHandler handler, bool add);

		[SuppressUnmanagedCodeSecurity]
		[DllImport("kernel32.dll")]
		private static extern IntPtr OpenThread(ThreadAccess dwDesiredAccess, bool bInheritHandle, uint dwThreadId);

		[SuppressUnmanagedCodeSecurity]
		[DllImport("kernel32.dll")]
		private static extern uint SuspendThread(IntPtr hThread);

		[SuppressUnmanagedCodeSecurity]
		[DllImport("kernel32.dll")]
		private static extern int ResumeThread(IntPtr hThread);

		[SuppressUnmanagedCodeSecurity]
		[DllImport("kernel32.dll")]
		private static extern int CloseHandle(IntPtr hThread);

		[DllImport("user32.dll", EntryPoint = "PostMessageA")]
		private static extern bool PostMessage(IntPtr hWnd, uint msg, int wParam, int lParam);

		[DllImport("kernel32.dll", CharSet = CharSet.None, ExactSpelling = false, SetLastError = true)]
		private static extern IntPtr GetStdHandle(int nStdHandle);

		[DllImport("kernel32.dll", CharSet = CharSet.None, ExactSpelling = false)]
		private static extern int GetFileType(SafeFileHandle handle);

		[DllImport("kernel32.dll", CharSet = CharSet.None, ExactSpelling = false, SetLastError = true)]
		private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out int mode);

		[DllImport("kernel32.dll", CharSet = CharSet.Ansi, EntryPoint = "GetStartupInfoA", SetLastError = true)]
		private static extern bool GetStartupInfo(out STARTUPINFO info);

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
		private struct STARTUPINFO {
			public uint cb;
			public string lpReserved;
			public string lpDesktop;
			public string lpTitle;
			public uint dwX;
			public uint dwY;
			public uint dwXSize;
			public uint dwYSize;
			public uint dwXCountChars;
			public uint dwYCountChars;
			public uint dwFillAttribute;
			public uint dwFlags;
			public ushort wShowWindow;
			public ushort cbReserved2;
			public IntPtr lpReserved2;
			public IntPtr hStdInput;
			public IntPtr hStdOutput;
			public IntPtr hStdError;
		}
	}
}