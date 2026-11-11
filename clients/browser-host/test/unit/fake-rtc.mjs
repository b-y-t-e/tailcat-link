// WebRTC in memory: just enough of `RTCPeerConnection` and its data channel
// for two ends to find each other through the signalling they are given.
//
// What it keeps honest is the part that is ours: a pair connects only once
// each end has the other's description *and* one of its candidates, so an
// offer, an answer and a candidate each have to cross the session for a
// channel to open. Everything ICE, DTLS and SCTP do is the browser's, and is
// checked against a real one by hand.

/// @param connects whether two ends that have signalled can reach each
///   other. False is two networks that cannot: everything is exchanged and no
///   channel ever opens.
export function fakeRtc({ connects = true } = {}) {
  const offers = new Map();
  const net = {
    connects,
    /// Every peer connection made, in order.
    connections: [],
    /// Messages handed to a channel.
    sent: 0,
    /// Ends every open channel without a word, as a network that went away.
    cut() {
      for (const pc of net.connections) pc.channel?._end();
    },
  };

  class FakeChannel {
    constructor(label) {
      this.label = label;
      this.readyState = "connecting";
      this.bufferedAmount = 0;
      this.peer = null;
      this.delivery = Promise.resolve();
    }

    send(data) {
      if (this.readyState !== "open") throw new Error("the channel is not open");
      net.sent++;
      // Copied, as a real channel does, and as an ArrayBuffer, which is what
      // `binaryType = "arraybuffer"` asks for.
      const copy = Uint8Array.from(data).buffer;
      const peer = this.peer;
      peer.delivery = peer.delivery.then(() => {
        if (peer.readyState === "open") peer.onmessage?.({ data: copy });
      });
    }

    close() {
      this._end();
      this.peer?._end();
    }

    _open() {
      if (this.readyState !== "connecting") return;
      this.readyState = "open";
      this.onopen?.();
    }

    _end() {
      if (this.readyState === "closed") return;
      this.readyState = "closed";
      this.onclose?.();
    }
  }

  class FakePeerConnection {
    constructor(configuration) {
      this.configuration = configuration;
      this.connectionState = "new";
      this.localDescription = null;
      this.remoteDescription = null;
      this.channel = null;
      this.other = null;
      this.candidates = 0;
      net.connections.push(this);
    }

    createDataChannel(label) {
      this.channel = new FakeChannel(label);
      return this.channel;
    }

    async createOffer() {
      this.id = offers.size + 1;
      offers.set(this.id, this);
      return { type: "offer", sdp: `fake offer ${this.id}` };
    }

    async createAnswer() {
      return { type: "answer", sdp: `fake answer ${this.other?.id}` };
    }

    async setLocalDescription(description) {
      this.localDescription = description;
      setTimeout(() => {
        if (this.connectionState === "closed") return;
        const candidate = { candidate: `candidate:fake ${net.connections.indexOf(this)}`, sdpMid: "0", sdpMLineIndex: 0 };
        this.onicecandidate?.({ candidate: { ...candidate, toJSON: () => candidate } });
        this.onicecandidate?.({ candidate: null });
      }, 0);
    }

    async setRemoteDescription(description) {
      if (description.type === "offer") {
        this.other = offers.get(Number(description.sdp.split(" ").pop())) ?? null;
        if (this.other) this.other.other = this;
      }
      this.remoteDescription = description;
      this.#connectIfSignalled();
    }

    async addIceCandidate(candidate) {
      if (!this.remoteDescription) throw new Error("a candidate arrived before the description it belongs to");
      if (!candidate?.candidate) throw new Error("not a candidate");
      this.candidates++;
      this.#connectIfSignalled();
      this.other?._connectIfSignalled();
    }

    _connectIfSignalled() {
      this.#connectIfSignalled();
    }

    #connectIfSignalled() {
      const other = this.other;
      if (!net.connects || !other) return;
      if (this.connectionState !== "new" || other.connectionState !== "new") return;
      if (!this.remoteDescription || !other.remoteDescription) return;
      if (!this.candidates || !other.candidates) return;

      const offerer = this.channel ? this : other;
      const answerer = offerer === this ? other : this;
      answerer.channel = new FakeChannel(offerer.channel.label);
      answerer.channel.peer = offerer.channel;
      offerer.channel.peer = answerer.channel;
      for (const pc of [offerer, answerer]) {
        pc.connectionState = "connected";
        pc.onconnectionstatechange?.();
      }
      setTimeout(() => {
        answerer.ondatachannel?.({ channel: answerer.channel });
        answerer.channel._open();
        offerer.channel._open();
      }, 0);
    }

    close() {
      if (this.connectionState === "closed") return;
      this.connectionState = "closed";
      this.channel?.close();
    }
  }

  net.RTCPeerConnection = FakePeerConnection;
  return net;
}
