use std::path::Path;

use anyhow::{Context, Result, bail};
use mcpmcp_wasi_component_spike::{compare_with_container, discover_wit, run_runtime_probe};

fn main() {
    if let Err(error) = run() {
        eprintln!("{error:#}");
        std::process::exit(1);
    }
}

fn run() -> Result<()> {
    let mut arguments = std::env::args().skip(1);
    match arguments.next().as_deref() {
        Some("discover") => {
            let path = arguments
                .next()
                .context("usage: mcpmcp-wasi-component-spike discover <wit-path> [world]")?;
            let world = arguments.next().unwrap_or_else(|| "connector".to_owned());
            if arguments.next().is_some() {
                bail!("unexpected extra argument");
            }
            let inventory = discover_wit(Path::new(&path), &world)?;
            println!("{}", serde_json::to_string_pretty(&inventory)?);
        }
        Some("probe") => {
            if arguments.next().is_some() {
                bail!("usage: mcpmcp-wasi-component-spike probe");
            }
            println!("{}", serde_json::to_string_pretty(&run_runtime_probe()?)?);
        }
        Some("compare-container") => {
            let image = arguments.next().context(
                "usage: mcpmcp-wasi-component-spike compare-container <image> [samples]",
            )?;
            let samples = arguments
                .next()
                .map(|value| value.parse())
                .transpose()
                .context("samples must be a positive integer")?
                .unwrap_or(7);
            if arguments.next().is_some() {
                bail!("unexpected extra argument");
            }
            println!(
                "{}",
                serde_json::to_string_pretty(&compare_with_container(&image, samples)?)?
            );
        }
        _ => {
            bail!(
                "usage:\n  mcpmcp-wasi-component-spike discover <wit-path> [world]\n  mcpmcp-wasi-component-spike probe\n  mcpmcp-wasi-component-spike compare-container <image> [samples]"
            );
        }
    }
    Ok(())
}
