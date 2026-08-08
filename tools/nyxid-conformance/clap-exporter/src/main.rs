mod cli;

mod auth {
    pub fn read_saved_base_url_for(_profile: Option<&str>) -> Option<String> {
        None
    }
}

use clap::{Command, CommandFactory};
use serde::Serialize;

#[derive(Serialize)]
struct CliLeafArtifact {
    schema_version: u32,
    command_name: String,
    top_level_count: usize,
    leaf_count: usize,
    leaves: Vec<CliLeaf>,
}

#[derive(Serialize)]
struct CliLeaf {
    path: String,
    about: String,
}

fn main() {
    let command = cli::Cli::command();
    let command_name = command.get_name().to_owned();
    let top_level_count = command.get_subcommands().count();
    let mut leaves = Vec::new();
    collect_leaves(&command, &mut Vec::new(), &mut leaves);
    leaves.sort_by(|left, right| left.path.cmp(&right.path));

    let artifact = CliLeafArtifact {
        schema_version: 1,
        command_name,
        top_level_count,
        leaf_count: leaves.len(),
        leaves,
    };
    serde_json::to_writer_pretty(std::io::stdout(), &artifact)
        .expect("CLI leaf artifact must serialize");
    println!();
}

fn collect_leaves(command: &Command, path: &mut Vec<String>, leaves: &mut Vec<CliLeaf>) {
    for child in command.get_subcommands() {
        path.push(child.get_name().to_owned());
        if child.has_subcommands() {
            collect_leaves(child, path, leaves);
        } else {
            leaves.push(CliLeaf {
                path: path.join(" "),
                about: child
                    .get_about()
                    .map(ToString::to_string)
                    .unwrap_or_default(),
            });
        }
        path.pop();
    }
}
