// The public surface. Everything else in this folder is how it is done.

export { TailcatLink } from "./link.js";
export {
  LinkClosedError,
  LinkError,
  LinkTimeoutError,
  PairingRefusedError,
  RemoteHandlerError,
} from "./errors.js";
export { ChannelCloseReason } from "./link-channel.js";
export { IndexedDbStore, memoryStore } from "./store.js";
export { parseAddress, parseInvitationCode } from "./address.js";
