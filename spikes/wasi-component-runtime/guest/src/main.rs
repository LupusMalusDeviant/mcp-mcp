// Minimaler WASI-P2-Guest. Nutzt bewusst echte WASI-Interfaces: `wasi:cli/environment` (env::var)
// und `wasi:cli/stdout` (println). Das mit `--target wasm32-wasip2` erzeugte Artefakt ist ein
// echtes WebAssembly Component und dient dem Spike als reales Import-Fixture (nicht nur benannt).
fn main() {
    let who = std::env::var("MCPMCP_SPIKE").unwrap_or_else(|_| "anonymous".to_owned());
    println!("mcpmcp-guest-ok:{who}");
}
