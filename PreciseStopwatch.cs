using System.Runtime.InteropServices;
using System.Security;

namespace System.Diagnostics {
	/// <summary>
	/// A flexible stopwatch for measuring elapsed time very precisely.
	/// </summary>
	public class PreciseStopwatch {
		private bool isRunning;
		private double elapsedTicks, startTimeStamp;
		/// <summary>
		/// Gets whether high resolution timing is supported on the current platform.
		/// </summary>
		public static readonly bool IsHighResolutionTimingSupported;
		/// <summary>
		/// Gets the amount of nanoseconds every platform-specific tick represents.
		/// </summary>
		public static readonly double NanosecondsPerTick;
		private double speedMultiplier = 1.0;

		[SuppressUnmanagedCodeSecurity]
		[DllImport("kernel32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool QueryPerformanceFrequency(out long PerformanceFrequency);

		[SuppressUnmanagedCodeSecurity]
		[DllImport("kernel32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool QueryPerformanceCounter(out long PerformanceCount);

		/// <summary>
		/// Gets or sets the stopwatch speed multiplier (1 means time speed is normal).
		/// </summary>
		public double SpeedMultiplier {
			get {
				return speedMultiplier;
			}
			set {
				if (value == speedMultiplier)
					return;
				speedMultiplier = value;
				if (isRunning) {
					elapsedTicks += (Timestamp - startTimeStamp) * speedMultiplier;
					startTimeStamp = Timestamp;
				}
			}
		}

		/// <summary>
		/// Gets or sets whether the stopwatch is started.
		/// </summary>
		public bool Running {
			get {
				return isRunning;
			}
			set {
				if (value == isRunning)
					return;
				isRunning = value;
				if (value)
					startTimeStamp = Timestamp;
				else
					elapsedTicks += (Timestamp - startTimeStamp) * speedMultiplier;
			}
		}

		/// <summary>
		/// Gets the elapsed time.
		/// </summary>
		public TimeSpan Elapsed {
			get {
				return new TimeSpan((long) Elapsed100NanosecondTicks);
			}
			set {
				Elapsed100NanosecondTicks = value.Ticks;
            }
		}

		/// <summary>
		/// Gets the elapsed time in platform-specific ticks.
		/// </summary>
		public double ElapsedTicks {
			get {
				return isRunning ? elapsedTicks + (Timestamp - startTimeStamp) * speedMultiplier : elapsedTicks;
			}
			set {
				elapsedTicks = value;
				startTimeStamp = Timestamp;
			}
		}

		/// <summary>
		/// Gets the elapsed time in fortnights.
		/// </summary>
		public double ElapsedFortnights {
			get {
				return (NanosecondsPerTick * 0.00000000000000082671957671957672) * ElapsedTicks;
			}
			set {
				ElapsedTicks = value * 1209600000000000.0 / NanosecondsPerTick;
			}
		}

		/// <summary>
		/// Gets the elapsed time in weeks.
		/// </summary>
		public double ElapsedWeeks {
			get {
				return (NanosecondsPerTick * 0.00000000000000165343915343915344) * ElapsedTicks;
			}
			set {
				ElapsedTicks = value * 604800000000000.0 / NanosecondsPerTick;
			}
		}

		/// <summary>
		/// Gets the elapsed time in days.
		/// </summary>
		public double ElapsedDays {
			get {
				return (NanosecondsPerTick * 0.00000000000001157407407407407407) * ElapsedTicks;
			}
			set {
				ElapsedTicks = value * 86400000000000.0 / NanosecondsPerTick;
			}
		}

		/// <summary>
		/// Gets the elapsed time in hours.
		/// </summary>
		public double ElapsedHours {
			get {
				return (NanosecondsPerTick * 0.00000000000027777777777777777777) * ElapsedTicks;
			}
			set {
				ElapsedTicks = value * 3600000000000.0 / NanosecondsPerTick;
			}
		}

		/// <summary>
		/// Gets the elapsed time in minutes.
		/// </summary>
		public double ElapsedMinutes {
			get {
				return (NanosecondsPerTick * 0.00000000001666666666666666666666) * ElapsedTicks;
			}
			set {
				ElapsedTicks = value * 60000000000.0 / NanosecondsPerTick;
			}
		}

		/// <summary>
		/// Gets the elapsed time in seconds.
		/// </summary>
		public double ElapsedSeconds {
			get {
				return (NanosecondsPerTick * 0.000000001) * ElapsedTicks;
			}
			set {
				ElapsedTicks = value * 1000000000.0 / NanosecondsPerTick;
			}
		}

		/// <summary>
		/// Gets the elapsed time in milliseconds.
		/// </summary>
		public double ElapsedMilliseconds {
			get {
				return (NanosecondsPerTick * 0.000001) * ElapsedTicks;
			}
			set {
				ElapsedTicks = value * 1000000.0 / NanosecondsPerTick;
			}
		}

		/// <summary>
		/// Gets the elapsed time in microseconds.
		/// </summary>
		public double ElapsedMicroseconds {
			get {
				return (NanosecondsPerTick * 0.001) * ElapsedTicks;
			}
			set {
				ElapsedTicks = value * 1000.0 / NanosecondsPerTick;
			}
		}

		/// <summary>
		/// Gets the elapsed time in standard ticks (not platform-specific).
		/// </summary>
		public double Elapsed100NanosecondTicks {
			get {
				return ElapsedTicks * (NanosecondsPerTick * 0.01);
			}
			set {
				ElapsedTicks = value * 100.0 / NanosecondsPerTick;
			}
		}

		/// <summary>
		/// Gets the elapsed time in nanoseconds.
		/// </summary>
		public double ElapsedNanoseconds {
			get {
				return NanosecondsPerTick * ElapsedTicks;
			}
			set {
				ElapsedTicks = value / NanosecondsPerTick;
			}
		}

		/// <summary>
		/// Gets the current time in platform-specific ticks.
		/// </summary>
		public static double Timestamp {
			get {
				if (IsHighResolutionTimingSupported) {
					long num;
					try {
						QueryPerformanceCounter(out num);
					} catch {
						num = 100;
					}
					return num;
				} else
					return DateTime.UtcNow.Ticks;
			}
		}

		static PreciseStopwatch() {
			long frequency = 10000000;
			try {
				IsHighResolutionTimingSupported = QueryPerformanceFrequency(out frequency);
			} catch {
				IsHighResolutionTimingSupported = false;
			}
			if (IsHighResolutionTimingSupported)
				NanosecondsPerTick = 1000000000.0 / frequency;
			else
				NanosecondsPerTick = 100.0;
		}

		/// <summary>
		/// Initializes a new high-resolution stopwatch.
		/// </summary>
		public PreciseStopwatch() {
		}

		/// <summary>
		/// Initializes a new high-resolution stopwatch with pre-elapsed ticks.
		/// </summary>
		/// <param name="preElapsedTicks">The starting elapsed ticks.</param>
		public PreciseStopwatch(double preElapsedTicks) {
			elapsedTicks = preElapsedTicks;
		}

		/// <summary>
		/// Initializes a new high-resolution stopwatch with pre-elapsed ticks.
		/// </summary>
		/// <param name="stopwatch">The stopwatch to clone.</param>
		public PreciseStopwatch(PreciseStopwatch stopwatch) {
			elapsedTicks = stopwatch.ElapsedTicks;
		}

		/// <summary>
		/// Initializes a new high-resolution stopwatch with pre-elapsed ticks.
		/// </summary>
		/// <param name="stopwatch">The stopwatch to clone.</param>
		public PreciseStopwatch(Stopwatch stopwatch) {
			elapsedTicks = stopwatch.ElapsedTicks;
		}

		/// <summary>
		/// Converts nanoseconds to platform-specific ticks.
		/// </summary>
		/// <param name="nanoseconds">The value of nanoseconds to convert to platform-specific ticks.</param>
		/// <returns>The equivalent of the nanoseconds in platform-specific ticks.</returns>
		public static double ConvertToTicks(double nanoseconds) {
			return nanoseconds / NanosecondsPerTick;
		}

		/// <summary>
		/// Converts platform-specific ticks to nanoseconds.
		/// </summary>
		/// <param name="ticks">The value of platform-specific ticks to convert to nanoseconds.</param>
		/// <returns>The equivalent of the platform-specific ticks in nanoseconds.</returns>
		public static double ConvertToNanoseconds(double ticks) {
			return ticks * NanosecondsPerTick;
		}
	}
}