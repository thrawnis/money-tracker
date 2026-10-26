/**
 * Browser side of passkeys. The server sends WebAuthn options as JSON, with
 * binary fields (challenge, ids) as base64url strings; the browser API wants
 * ArrayBuffers, and its result has to go back as the same JSON shape.
 */

export const passkeysSupported = () =>
  typeof window !== 'undefined' && typeof window.PublicKeyCredential === 'function'
  && !!navigator.credentials?.create;

const toBytes = (b64url: string): ArrayBuffer => {
  const b64 = b64url.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(b64url.length / 4) * 4, '=');
  const bin = atob(b64);
  const bytes = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
  return bytes.buffer;
};

const toB64Url = (buf: ArrayBuffer | ArrayBufferView | null | undefined): string | undefined => {
  if (!buf) return undefined;
  const bytes = buf instanceof ArrayBuffer ? new Uint8Array(buf) : new Uint8Array(buf.buffer, buf.byteOffset, buf.byteLength);
  let bin = '';
  for (const b of bytes) bin += String.fromCharCode(b);
  return btoa(bin).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
};

// Drop nulls: the browser rejects e.g. "extensions": null.
const clean = <T,>(o: T): T => JSON.parse(JSON.stringify(o, (_k, v) => (v === null ? undefined : v)));

type Json = Record<string, unknown>;

function creationOptions(json: Json): CredentialCreationOptions {
  const o = clean(json) as Json & {
    challenge: string; user: Json & { id: string }; excludeCredentials?: (Json & { id: string })[];
  };
  return {
    publicKey: {
      ...(o as object),
      challenge: toBytes(o.challenge),
      user: { ...o.user, id: toBytes(o.user.id) },
      excludeCredentials: (o.excludeCredentials ?? []).map(c => ({ ...c, id: toBytes(c.id) })),
    } as PublicKeyCredentialCreationOptions,
  };
}

function requestOptions(json: Json): CredentialRequestOptions {
  const o = clean(json) as Json & { challenge: string; allowCredentials?: (Json & { id: string })[] };
  return {
    publicKey: {
      ...(o as object),
      challenge: toBytes(o.challenge),
      allowCredentials: (o.allowCredentials ?? []).map(c => ({ ...c, id: toBytes(c.id) })),
    } as PublicKeyCredentialRequestOptions,
  };
}

// The credential as WebAuthn JSON (what PublicKeyCredential.toJSON() gives in
// newer browsers), built by hand where that isn't available.
function credentialJson(cred: PublicKeyCredential): string {
  const withToJson = cred as PublicKeyCredential & { toJSON?: () => unknown };
  if (typeof withToJson.toJSON === 'function') return JSON.stringify(withToJson.toJSON());
  const r = cred.response as AuthenticatorAttestationResponse & AuthenticatorAssertionResponse;
  const response: Record<string, unknown> = { clientDataJSON: toB64Url(r.clientDataJSON) };
  if ('attestationObject' in r && r.attestationObject) {
    response.attestationObject = toB64Url(r.attestationObject);
    response.transports = typeof r.getTransports === 'function' ? r.getTransports() : [];
  } else {
    response.authenticatorData = toB64Url(r.authenticatorData);
    response.signature = toB64Url(r.signature);
    response.userHandle = toB64Url(r.userHandle);
  }
  return JSON.stringify({
    id: cred.id,
    rawId: toB64Url(cred.rawId),
    type: cred.type,
    response,
    clientExtensionResults: cred.getClientExtensionResults?.() ?? {},
    authenticatorAttachment: cred.authenticatorAttachment ?? undefined,
  });
}

/** Turns a browser/authenticator failure into something to show the user. */
export function passkeyErrorMessage(err: unknown): string {
  const name = (err as { name?: string })?.name;
  if (name === 'NotAllowedError') return 'The passkey prompt was cancelled or timed out.';
  if (name === 'InvalidStateError') return 'This device already has a passkey for your account.';
  if (name === 'SecurityError') return 'Passkeys need this site to be opened over HTTPS (or on localhost).';
  if (name === 'NotSupportedError') return "This browser or device doesn't support passkeys.";
  return (err as { response?: { data?: { message?: string } } })?.response?.data?.message
    ?? 'Something went wrong with the passkey. Please try again.';
}

/** Creates a passkey from server options; returns the attestation JSON for the server. */
export async function createPasskey(optionsJson: Json): Promise<string> {
  const cred = await navigator.credentials.create(creationOptions(optionsJson));
  if (!cred) throw Object.assign(new Error('No passkey was created.'), { name: 'NotAllowedError' });
  return credentialJson(cred as PublicKeyCredential);
}

/** Signs in with a passkey from server options; returns the assertion JSON for the server. */
export async function getPasskey(optionsJson: Json): Promise<string> {
  const cred = await navigator.credentials.get(requestOptions(optionsJson));
  if (!cred) throw Object.assign(new Error('No passkey was used.'), { name: 'NotAllowedError' });
  return credentialJson(cred as PublicKeyCredential);
}

/** A friendly default name for a new passkey, from the browser/OS. */
export function defaultPasskeyName(): string {
  const ua = navigator.userAgent;
  const os = /iPhone|iPad/.test(ua) ? 'iPhone/iPad' : /Android/.test(ua) ? 'Android' : /Mac OS X/.test(ua) ? 'Mac'
    : /Windows/.test(ua) ? 'Windows' : /Linux/.test(ua) ? 'Linux' : 'This device';
  const browser = /Edg\//.test(ua) ? 'Edge' : /Chrome\//.test(ua) ? 'Chrome' : /Safari\//.test(ua) ? 'Safari'
    : /Firefox\//.test(ua) ? 'Firefox' : '';
  return browser ? `${os} · ${browser}` : os;
}
