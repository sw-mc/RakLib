namespace SkyWing.RakLib.server; 

public interface ProtocolAcceptor {

	public bool Accepts(int protocolVersion);

	public int GetPrimaryVersion();
}

public sealed class SimpleProtocolAcceptor : ProtocolAcceptor {

	public int ProtocolVersion {
		get => _protocolVersion;
		init {
			if (_protocolVersion < 0) throw new ArgumentOutOfRangeException(nameof(value));
			_protocolVersion = value;
		}
	}

	private int _protocolVersion;
	
	public SimpleProtocolAcceptor(int protocolVersion) {
		ProtocolVersion = protocolVersion;
	}

	public bool Accepts(int protocolVersion) {
		return protocolVersion == ProtocolVersion;
	}

    public int GetPrimaryVersion() {
        throw new NotImplementedException();
    }
}