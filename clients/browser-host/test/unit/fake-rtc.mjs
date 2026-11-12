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
/// @param delayMs how long a message is on its way; what is on its way when
///   the network is cut never arrives.
export function fakeRtc({ connects = true, delayMs = 0 } = {}) {
  const offers = new Map();
  const net = {
    connects,
    /// Every peer connection made, in order.
    connections: [],
    /// Messages handed to a channel.
    sent: 0,
    /// Ends every open channel without a word, as a network that went away:
    /// ICE notices first, as it does for real, and then the channels go.
    cut() {
      for (const pc of net.connections) {
        if (pc.connectionState !== "connected") continue;
        pc.connectionState = "failed";
        pc.onconnectionstatechange?.();
      }
      for (const pc of net.connections) pc.channel?._end();
    },
    /// The same for one end only, as a network that went away where the other
    /// end cannot hear it: what that end sends is lost, and its ICE never says.
    cutOne(pc) {
      pc.unheard = true;
      pc.connectionState = "failed";
      pc.onconnectionstatechange?.();
      pc.channel?._end();
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
      // `binaryType = "arraybuffer"` asks for; text stays text.
      const copy = typeof data === "string" ? data : Uint8Array.from(data).buffer;
      const peer = this.peer;
      const wait = delayMs ? () => new Promise((resolve) => setTimeout(resolve, delayMs)) : () => {};
      peer.delivery = peer.delivery.then(wait).then(() => {
        if (peer.readyState === "open") peer.onmessage?.({ data: copy });
      });
    }

    close() {
      this._end();
      // Behind whatever was sent before it, as a real channel's close is.
      const peer = this.peer;
      if (peer) peer.delivery = peer.delivery.then(() => peer._end());
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
      // What a real stack does at the other end: ICE goes before the channel
      // says so. The double used to keep it up, and a check that relied on
      // that passed here and failed for real.
      // Unless the network between them is gone, which nothing crosses.
      if (this.unheard) return;
      const other = this.other;
      if (other && other.connectionState === "connected") {
        other.connectionState = "failed";
        other.onconnectionstatechange?.();
      }
      this.channel?.close();
    }
  }

  net.RTCPeerConnection = FakePeerConnection;
  return net;
}
