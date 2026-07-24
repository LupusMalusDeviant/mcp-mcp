//! WP1 (Plan 0003, ADR-0020): IPC-Host für die WASI-Runtime.
//!
//! Der Rust-Host spricht mit dem .NET-Gateway über **length-prefixed JSON über stdio**: jede
//! Nachricht ist ein 4-Byte-Big-Endian-Längenpräfix gefolgt von einem JSON-Body. `stdout` gehört
//! dem Protokoll (Logs strikt auf `stderr`, wie die MCP-stdio-Server). Die Verarbeitung liegt in
//! einer reinen, testbaren [`Session`]; der Loop macht nur IO. `load`/`invoke` folgen als nächste
//! WP1-Schritte — dieser Stand deckt Framing, Handshake mit Versionsverhandlung, `health` und
//! `shutdown` ab.

use std::io::{self, Read, Write};

use anyhow::{Result, bail};
use base64::Engine as _;
use base64::engine::general_purpose::STANDARD as BASE64;
use ed25519_dalek::VerifyingKey;
use serde::{Deserialize, Serialize};

use crate::{
    CapabilityGrants, GrantAuditRecord, RUNTIME_VERSION, grant_audit_record, pinned_publisher,
    run_wasi_component, verify_component_signature,
};

/// Protokollversion des IPC-Vertrags. Inkompatible Versionen werden beim Handshake abgewiesen.
pub const PROTOCOL_VERSION: &str = "1";

/// Obergrenze für einen einzelnen Frame (Schutz gegen Memory-DoS über ein riesiges Längenpräfix).
const MAX_FRAME_BYTES: u32 = 64 * 1024 * 1024;

/// Anfrage vom Gateway an den Host.
#[derive(Debug, Deserialize)]
#[serde(tag = "type", rename_all = "kebab-case")]
pub enum Request {
    /// Handshake mit Versionsverhandlung.
    Hello {
        #[serde(rename = "protocolVersion")]
        protocol_version: String,
    },
    /// Lädt ein Component: Signatur gegen die gepinnten Publisher prüfen, Grants übernehmen.
    /// Alle Byte-Felder sind Base64 (JSON-tauglich, keine Sonderzeichen).
    Load {
        /// Component-Bytes, Base64.
        component: String,
        /// Detached Ed25519-Signatur (64 Byte), Base64.
        signature: String,
        /// Administrativ gepinnte Publisher-Public-Keys (je 32 Byte), Base64.
        #[serde(rename = "pinnedPublishers")]
        pinned_publishers: Vec<String>,
        /// Erteilte Host-Grants; fehlend = default-deny.
        #[serde(default)]
        grants: CapabilityGrants,
    },
    /// Führt das geladene Component aus.
    Invoke,
    /// Liveness/Readiness-Abfrage.
    Health,
    /// Ordentlicher Shutdown.
    Shutdown,
}

/// Antwort des Hosts an das Gateway.
#[derive(Debug, Deserialize, PartialEq, Serialize)]
#[serde(tag = "type", rename_all = "kebab-case")]
pub enum Response {
    Hello {
        #[serde(rename = "protocolVersion")]
        protocol_version: String,
        runtime: String,
        host: String,
    },
    Loaded {
        audit: GrantAuditRecord,
    },
    Invoked {
        stdout: String,
    },
    Health {
        status: String,
        loaded: bool,
    },
    Bye,
    Error {
        code: String,
        message: String,
    },
}

/// Steuert, ob der Loop nach einer Antwort weiterläuft oder ordentlich endet.
#[derive(Debug, PartialEq)]
pub enum Control {
    Continue,
    Stop,
}

/// Zustand einer IPC-Sitzung. Die Logik ist rein (kein IO) und damit direkt testbar.
#[derive(Debug, Default)]
pub struct Session {
    negotiated: bool,
    loaded: Option<LoadedComponent>,
}

/// Ein verifiziertes, geladenes Component samt den dafür erteilten Grants.
#[derive(Debug)]
struct LoadedComponent {
    bytes: Vec<u8>,
    grants: CapabilityGrants,
}

fn error(code: &str, message: impl Into<String>) -> (Response, Control) {
    (
        Response::Error {
            code: code.to_owned(),
            message: message.into(),
        },
        Control::Continue,
    )
}

impl Session {
    /// Verarbeitet eine Anfrage zu einer Antwort plus Loop-Steuerung.
    pub fn handle(&mut self, request: Request) -> (Response, Control) {
        match request {
            Request::Hello { protocol_version } => {
                if protocol_version != PROTOCOL_VERSION {
                    return (
                        Response::Error {
                            code: "unsupported-protocol".to_owned(),
                            message: format!(
                                "host spricht Protokoll {PROTOCOL_VERSION}, Client bot {protocol_version}"
                            ),
                        },
                        Control::Continue,
                    );
                }
                self.negotiated = true;
                (
                    Response::Hello {
                        protocol_version: PROTOCOL_VERSION.to_owned(),
                        runtime: RUNTIME_VERSION.to_owned(),
                        host: format!("mcpmcp-wasi-host/{}", env!("CARGO_PKG_VERSION")),
                    },
                    Control::Continue,
                )
            }
            Request::Load {
                component,
                signature,
                pinned_publishers,
                grants,
            } => {
                if !self.negotiated {
                    return error("handshake-required", "hello muss vor load gesendet werden");
                }
                match self.load(&component, &signature, &pinned_publishers, grants) {
                    Ok(audit) => (Response::Loaded { audit }, Control::Continue),
                    Err(failure) => error("load-rejected", failure.to_string()),
                }
            }
            Request::Invoke => {
                let Some(loaded) = self.loaded.as_ref() else {
                    return error("not-loaded", "kein Component geladen — load zuerst senden");
                };
                match run_wasi_component(&loaded.bytes, &loaded.grants) {
                    Ok(stdout) => (Response::Invoked { stdout }, Control::Continue),
                    Err(failure) => error("invoke-failed", failure.to_string()),
                }
            }
            Request::Health => (
                Response::Health {
                    status: "ok".to_owned(),
                    loaded: self.loaded.is_some(),
                },
                Control::Continue,
            ),
            Request::Shutdown => (Response::Bye, Control::Stop),
        }
    }

    /// Dekodiert, verifiziert und übernimmt ein Component. Fail-closed: ohne gültige Signatur
    /// eines gepinnten Publishers wird nichts geladen und der bisherige Zustand bleibt unberührt.
    fn load(
        &mut self,
        component_b64: &str,
        signature_b64: &str,
        pinned_b64: &[String],
        grants: CapabilityGrants,
    ) -> Result<GrantAuditRecord> {
        let component = BASE64.decode(component_b64)?;
        let signature: [u8; 64] = BASE64
            .decode(signature_b64)?
            .try_into()
            .map_err(|_| anyhow::anyhow!("Signatur muss genau 64 Byte lang sein"))?;

        if pinned_b64.is_empty() {
            bail!("kein gepinnter Publisher übergeben — fail-closed");
        }
        let mut pinned = Vec::with_capacity(pinned_b64.len());
        for encoded in pinned_b64 {
            let key: [u8; 32] = BASE64
                .decode(encoded)?
                .try_into()
                .map_err(|_| anyhow::anyhow!("Publisher-Key muss genau 32 Byte lang sein"))?;
            pinned.push(pinned_publisher(VerifyingKey::from_bytes(&key)?));
        }

        let publisher_key_id = verify_component_signature(&component, &signature, &pinned)?;
        let audit = grant_audit_record(&component, &publisher_key_id, &grants);
        self.loaded = Some(LoadedComponent {
            bytes: component,
            grants,
        });
        Ok(audit)
    }
}

/// Schreibt eine Antwort als gerahmten Frame (Längenpräfix + JSON) und flusht.
pub fn write_frame<W: Write>(writer: &mut W, response: &Response) -> Result<()> {
    let body = serde_json::to_vec(response)?;
    let len = u32::try_from(body.len())?;
    writer.write_all(&len.to_be_bytes())?;
    writer.write_all(&body)?;
    writer.flush()?;
    Ok(())
}

/// Liest einen Frame-Body (ohne Längenpräfix). `Ok(None)` bei sauberem EOF vor dem nächsten Frame.
fn read_frame_bytes<R: Read>(reader: &mut R) -> Result<Option<Vec<u8>>> {
    let mut len_buf = [0u8; 4];
    match reader.read_exact(&mut len_buf) {
        Ok(()) => {}
        Err(error) if error.kind() == io::ErrorKind::UnexpectedEof => return Ok(None),
        Err(error) => return Err(error.into()),
    }

    let len = u32::from_be_bytes(len_buf);
    if len > MAX_FRAME_BYTES {
        bail!("frame of {len} bytes exceeds the {MAX_FRAME_BYTES}-byte limit");
    }

    let mut body = vec![0u8; len as usize];
    reader.read_exact(&mut body)?;
    Ok(Some(body))
}

/// Die IPC-Schleife: Frames lesen, in der `Session` verarbeiten, Antworten rahmen. Ein
/// unparsbarer Frame ergibt eine `error`-Antwort und beendet den Host NICHT; `shutdown` und EOF
/// beenden ihn sauber.
pub fn serve<R: Read, W: Write>(reader: &mut R, writer: &mut W) -> Result<()> {
    let mut session = Session::default();
    while let Some(body) = read_frame_bytes(reader)? {
        match serde_json::from_slice::<Request>(&body) {
            Ok(request) => {
                let (response, control) = session.handle(request);
                write_frame(writer, &response)?;
                if control == Control::Stop {
                    return Ok(());
                }
            }
            Err(error) => {
                write_frame(
                    writer,
                    &Response::Error {
                        code: "bad-request".to_owned(),
                        message: error.to_string(),
                    },
                )?;
            }
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use ed25519_dalek::{Signer, SigningKey};

    use super::*;

    const GUEST: &[u8] = include_bytes!("../fixtures/wasi-p2-guest.component.wasm");

    /// Session nach erfolgreichem Handshake.
    fn negotiated() -> Session {
        let mut session = Session::default();
        session.handle(Request::Hello {
            protocol_version: PROTOCOL_VERSION.to_owned(),
        });
        session
    }

    fn load_request(signing: &SigningKey, grants: CapabilityGrants) -> Request {
        Request::Load {
            component: BASE64.encode(GUEST),
            signature: BASE64.encode(signing.sign(GUEST).to_bytes()),
            pinned_publishers: vec![BASE64.encode(signing.verifying_key().as_bytes())],
            grants,
        }
    }

    fn frame(bytes: &[u8]) -> Vec<u8> {
        let len = u32::try_from(bytes.len()).unwrap();
        let mut framed = len.to_be_bytes().to_vec();
        framed.extend_from_slice(bytes);
        framed
    }

    fn first_response(input: &[u8]) -> Response {
        let mut output = Vec::new();
        serve(&mut &input[..], &mut output).unwrap();
        let len = u32::from_be_bytes(output[..4].try_into().unwrap()) as usize;
        serde_json::from_slice(&output[4..4 + len]).unwrap()
    }

    #[test]
    fn handshake_accepts_matching_version() {
        let (response, control) = Session::default().handle(Request::Hello {
            protocol_version: PROTOCOL_VERSION.to_owned(),
        });
        assert_eq!(control, Control::Continue);
        match response {
            Response::Hello {
                protocol_version,
                runtime,
                host,
            } => {
                assert_eq!(protocol_version, PROTOCOL_VERSION);
                assert!(runtime.contains("wasmtime"));
                assert!(host.starts_with("mcpmcp-wasi-host/"));
            }
            other => panic!("expected hello, got {other:?}"),
        }
    }

    #[test]
    fn handshake_rejects_mismatched_version() {
        let (response, _) = Session::default().handle(Request::Hello {
            protocol_version: "999".to_owned(),
        });
        assert!(matches!(
            response,
            Response::Error { code, .. } if code == "unsupported-protocol"
        ));
    }

    #[test]
    fn shutdown_stops_the_loop() {
        let (response, control) = Session::default().handle(Request::Shutdown);
        assert_eq!(response, Response::Bye);
        assert_eq!(control, Control::Stop);
    }

    #[test]
    fn health_reports_not_loaded_initially() {
        let (response, control) = Session::default().handle(Request::Health);
        assert_eq!(control, Control::Continue);
        assert_eq!(
            response,
            Response::Health {
                status: "ok".to_owned(),
                loaded: false,
            }
        );
    }

    #[test]
    fn load_requires_a_handshake_first() {
        let signing = SigningKey::from_bytes(&[3u8; 32]);
        let (response, _) =
            Session::default().handle(load_request(&signing, CapabilityGrants::default()));
        assert!(matches!(response, Response::Error { code, .. } if code == "handshake-required"));
    }

    #[test]
    fn load_verifies_signature_and_returns_the_audit_record() {
        let signing = SigningKey::from_bytes(&[3u8; 32]);
        let mut session = negotiated();

        let (response, control) =
            session.handle(load_request(&signing, CapabilityGrants::default()));

        assert_eq!(control, Control::Continue);
        match response {
            Response::Loaded { audit } => {
                assert_eq!(audit.module_sha256, crate::sha256_hex(GUEST));
                assert_eq!(audit.runtime, RUNTIME_VERSION);
                assert!(audit.granted_filesystem_preopens.is_empty());
            }
            other => panic!("expected loaded, got {other:?}"),
        }
        // health spiegelt den Ladezustand.
        let (health, _) = session.handle(Request::Health);
        assert_eq!(
            health,
            Response::Health {
                status: "ok".to_owned(),
                loaded: true,
            }
        );
    }

    #[test]
    fn load_is_rejected_for_an_unpinned_publisher() {
        let trusted = SigningKey::from_bytes(&[1u8; 32]);
        let rogue = SigningKey::from_bytes(&[2u8; 32]);
        let mut session = negotiated();

        let (response, _) = session.handle(Request::Load {
            component: BASE64.encode(GUEST),
            signature: BASE64.encode(rogue.sign(GUEST).to_bytes()),
            pinned_publishers: vec![BASE64.encode(trusted.verifying_key().as_bytes())],
            grants: CapabilityGrants::default(),
        });

        assert!(matches!(response, Response::Error { code, .. } if code == "load-rejected"));
    }

    #[test]
    fn load_without_any_pinned_publisher_fails_closed() {
        let signing = SigningKey::from_bytes(&[3u8; 32]);
        let mut session = negotiated();

        let (response, _) = session.handle(Request::Load {
            component: BASE64.encode(GUEST),
            signature: BASE64.encode(signing.sign(GUEST).to_bytes()),
            pinned_publishers: vec![],
            grants: CapabilityGrants::default(),
        });

        assert!(matches!(response, Response::Error { code, .. } if code == "load-rejected"));
    }

    #[test]
    fn invoke_without_load_is_rejected() {
        let (response, _) = negotiated().handle(Request::Invoke);
        assert!(matches!(response, Response::Error { code, .. } if code == "not-loaded"));
    }

    #[test]
    fn invoke_runs_only_with_granted_capabilities() {
        let signing = SigningKey::from_bytes(&[3u8; 32]);

        // Ohne Grant: WASI wird nicht gelinkt -> Ausfuehrung scheitert vor dem Start.
        let mut denied = negotiated();
        denied.handle(load_request(&signing, CapabilityGrants::default()));
        let (response, _) = denied.handle(Request::Invoke);
        assert!(matches!(response, Response::Error { code, .. } if code == "invoke-failed"));

        // Mit Environment-Grant laeuft die echte Component.
        let mut grants = CapabilityGrants::default();
        grants.environment.insert("MCPMCP_SPIKE".to_owned());
        let mut allowed = negotiated();
        allowed.handle(load_request(&signing, grants));
        let (response, _) = allowed.handle(Request::Invoke);
        match response {
            Response::Invoked { stdout } => assert!(stdout.contains("mcpmcp-guest-ok")),
            other => panic!("expected invoked, got {other:?}"),
        }
    }

    #[test]
    fn serve_frames_a_hello_response_then_ends_on_eof() {
        let input = frame(br#"{"type":"hello","protocolVersion":"1"}"#);
        assert!(matches!(first_response(&input), Response::Hello { .. }));
    }

    #[test]
    fn serve_rejects_a_malformed_frame_without_dying() {
        let mut input = frame(b"not valid json");
        input.extend(frame(br#"{"type":"shutdown"}"#));
        let mut output = Vec::new();
        serve(&mut &input[..], &mut output).unwrap();

        // Erste Antwort: bad-request; der Host lebt weiter und verarbeitet das folgende shutdown.
        let first_len = u32::from_be_bytes(output[..4].try_into().unwrap()) as usize;
        let first: Response = serde_json::from_slice(&output[4..4 + first_len]).unwrap();
        assert!(matches!(first, Response::Error { code, .. } if code == "bad-request"));
        let rest = &output[4 + first_len..];
        let second_len = u32::from_be_bytes(rest[..4].try_into().unwrap()) as usize;
        let second: Response = serde_json::from_slice(&rest[4..4 + second_len]).unwrap();
        assert_eq!(second, Response::Bye);
    }
}
