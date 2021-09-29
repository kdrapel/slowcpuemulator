namespace System.Windows.Forms {
	/// <summary>
	/// Represents a global hotkey.
	/// </summary>
	public struct Hotkey : IEquatable<Hotkey>, ICloneable {
		/// <summary>
		/// Represents an empty hotkey.
		/// </summary>
		public static readonly Hotkey Empty = new Hotkey();
		/// <summary>
		/// The key that triggers the hotkey if accompanied by the assigned modifiers.
		/// </summary>
		public readonly Keys Key;
		/// <summary>
		/// The modifiers that must accompany the key in order to trigger the hotkey.
		/// </summary>
		public readonly KeyModifiers Modifiers;
		internal int id;

		/// <summary>
		/// Gets the true hash of the hotkey.
		/// </summary>
		[CLSCompliant(false)]
		public uint TrueHash {
			get {
				return ((uint) Key << 16) | (uint) Modifiers;
			}
		}

		/// <summary>
		/// Initializes a hotkey from the specified parameters.
		/// </summary>
		/// <param name="key">The key that triggers the hotkey if accompanied by the assigned modifiers.</param>
		/// <param name="modifiers">The modifiers that must accompany the key in order to trigger the hotkey.</param>
		public Hotkey(Keys key, KeyModifiers modifiers) {
			Key = key;
			Modifiers = modifiers;
			id = 0;
		}

		/// <summary>
		/// Initializes a hotkey from the specified true hash.
		/// </summary>
		/// <param name="trueHash">The true hash of the hotkey.</param>
		[CLSCompliant(false)]
		public Hotkey(uint trueHash) {
			Key = (Keys) ((trueHash & 0xffff0000) >> 16);
			Modifiers = (KeyModifiers) (trueHash & 0x0000ffff);
			id = 0;
		}

		/// <summary>
		/// Clones the specified hotkey.
		/// </summary>
		/// <param name="key">The hotkey to clone.</param>
		public Hotkey(Hotkey key) {
			Key = key.Key;
			Modifiers = key.Modifiers;
			id = key.id;
		}

		/// <summary>
		/// Returns whether this hotkey instance and the object are considered equal.
		/// </summary>
		/// <param name="obj">The object to compare with.</param>
		public override bool Equals(object obj) {
			return obj is Hotkey ? Equals((Hotkey) obj) : false;
		}

		/// <summary>
		/// Returns whether this hotkey is equal to the specified hotkey.
		/// </summary>
		/// <param name="key">The hotkey to compare with.</param>
		public bool Equals(Hotkey key) {
			return id == 0 || key.id == 0 ? (Key == key.Key && (Modifiers | KeyModifiers.NoRepeat) == (key.Modifiers | KeyModifiers.NoRepeat)) : (id == key.id);
		}

		/// <summary>
		/// Returns whether two hotkeys are considered equal.
		/// </summary>
		public static bool operator ==(Hotkey a, Hotkey b) {
			return a.Equals(b);
		}

		/// <summary>
		/// Returns whether two hotkeys are considered different.
		/// </summary>
		public static bool operator !=(Hotkey a, Hotkey b) {
			return !(a == b);
		}

		/// <summary>
		/// Clones the hotkey instance.
		/// </summary>
		public object Clone() {
			return new Hotkey(this);
		}

		/// <summary>
		/// Gets the hash code of the hotkey.
		/// </summary>
		public override int GetHashCode() {
			return (int) TrueHash;
		}

		/// <summary>
		/// Gets a string that represents the hotkey.
		/// </summary>
		public override string ToString() {
			return Modifiers.ToString().Replace(" ", "").Replace(',', '+').Replace("+NoRepeat", "") + "+" + Key.ToString().Replace(" ", "").Replace(',', '+');
		}
	}

	/// <summary>
	/// Represents the key modifiers that can be used to trigger hotkeys.
	/// </summary>
	[Flags]
	public enum KeyModifiers : int {
		/// <summary>
		/// No modifier
		/// </summary>
		None = 0,
		/// <summary>
		/// Alt key
		/// </summary>
		Alt = 1,
		/// <summary>
		/// Control key
		/// </summary>
		Control = 2,
		/// <summary>
		/// Shift key
		/// </summary>
		Shift = 4,
		/// <summary>
		/// Windows Start key
		/// </summary>
		Windows = 8,
		/// <summary>
		/// Disables press and hold to repeat for the hotkey.
		/// </summary>
		NoRepeat = 16384
	}
}