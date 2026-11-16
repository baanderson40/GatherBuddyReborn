//! Independent reference exporter. Never calls GatherBuddy's simulator or search.
use raphael_data::{CrafterStats, RECIPES, RLVLS, get_game_settings};
use raphael_sim::{Action, Combo, Condition, Settings, SimulationState};
use raphael_solver::{AtomicFlag, MacroSolver, SolverSettings};
use serde_json::{Value, json};
use std::io::Write;
use std::sync::{Arc, Mutex, mpsc};
use std::time::Duration;

const ACTIONS: &[Action] = &[
    Action::BasicSynthesis, Action::CarefulSynthesis, Action::Groundwork,
    Action::IntensiveSynthesis, Action::PrudentSynthesis, Action::MuscleMemory,
    Action::DelicateSynthesis, Action::Veneration, Action::MasterMend,
    Action::Manipulation, Action::WasteNot, Action::WasteNot2,
    Action::TrainedPerfection, Action::ImmaculateMend, Action::TricksOfTheTrade,
    Action::Reflect, Action::TrainedEye, Action::BasicTouch, Action::StandardTouch,
    Action::AdvancedTouch, Action::PreparatoryTouch, Action::PreciseTouch,
    Action::PrudentTouch, Action::RefinedTouch, Action::TrainedFinesse,
    Action::ByregotsBlessing, Action::Innovation, Action::GreatStrides, Action::Observe,
];

fn state_json(state: SimulationState, condition: Condition, settings: &Settings) -> Value {
    let e = state.effects;
    // Observe and Standard Touch are the same discount state in Raphael. Other
    // action-history differences are not represented; tests compare this projection only.
    let combo = match e.combo() {
        Combo::BasicTouch => Action::BasicTouch.action_id(),
        Combo::StandardTouch => Action::StandardTouch.action_id(),
        _ => 0,
    };
    json!([
        e.combo() == Combo::SynthesisBegin, state.progress, state.quality.min(settings.max_quality),
        state.durability, state.cp, condition as u8, e.inner_quiet(), e.waste_not(),
        e.manipulation(), e.great_strides(), e.innovation(), e.veneration(), e.muscle_memory(),
        0, e.trained_perfection_available(), e.trained_perfection_active(), combo
    ])
}

fn next_condition(current: Condition) -> Condition {
    match current { Condition::Excellent => Condition::Poor, _ => Condition::Normal }
}

fn macro_reference(settings: Settings) -> Value {
    let latest = Arc::new(Mutex::new(None::<Vec<Action>>));
    let callback_latest = latest.clone();
    let interrupt = AtomicFlag::new();
    let timer_interrupt = interrupt.clone();
    let (done, receiver) = mpsc::channel();
    // Bound expensive reference search; intermediate validated macros are retained.
    let timer = std::thread::spawn(move || {
        if receiver.recv_timeout(Duration::from_secs(2)).is_err() { timer_interrupt.set(); }
    });
    let mut solver = MacroSolver::new(
        SolverSettings { simulator_settings: settings, allow_non_max_quality_solutions: true },
        Box::new(move |actions| *callback_latest.lock().unwrap() = Some(actions.to_vec())),
        Box::new(|_| {}), interrupt,
    );
    let solved = solver.solve();
    let _ = done.send(());
    timer.join().unwrap();
    let actions = match &solved {
        Ok(actions) => Some(actions.clone()),
        Err(_) => latest.lock().unwrap().clone(),
    };
    match actions {
        Some(actions) => {
            let final_state = SimulationState::from_macro(&settings, &actions).unwrap();
            assert!(final_state.progress >= settings.max_progress);
            json!({
                "Status": if solved.is_ok() { "Complete" } else { "InterruptedWithIncumbent" },
                "Actions": actions.iter().map(|a| a.action_id()).collect::<Vec<_>>(),
                "FinalQuality": final_state.quality.min(settings.max_quality),
                "Duration": actions.iter().map(|a| u32::from(a.time_cost())).sum::<u32>(),
                "FinalProgress": final_state.progress,
            })
        }
        None => json!({"Status": format!("NoMacro:{:?}", solved.err()), "Actions": []}),
    }
}

fn write(writer: &mut impl Write, value: Value) {
    serde_json::to_writer(&mut *writer, &value).unwrap();
    writeln!(writer).unwrap();
}

fn main() {
    let args: Vec<_> = std::env::args().collect();
    assert_eq!(args.len(), 3, "Usage: adaptive-reference OUTPUT_JSONL VERIFIED_SOURCE_COMMIT");
    assert_eq!(args[2].len(), 40);
    let mut writer = std::io::BufWriter::new(std::fs::File::create(&args[1]).unwrap());
    write(&mut writer, json!({
        "Kind": "Header", "SchemaVersion": 1, "GeneratorVersion": 1, "SourceCommit": args[2],
        "SourceRepository": "https://github.com/KonaeAkira/raphael-rs.git",
        "MacroTimeoutSeconds": 2,
        "StateColumns": ["FirstStep", "Progress", "Quality", "Durability", "CP", "Condition",
            "InnerQuiet", "WasteNot", "Manipulation", "GreatStrides", "Innovation", "Veneration",
            "MuscleMemory", "FinalAppraisal", "TrainedPerfectionAvailable", "TrainedPerfectionActive", "Combo"],
        "Projection": "Quality clamped; durability saturates at zero; Observe=StandardTouch combo; terminal effects not compared; FinalAppraisal absent in Raphael",
    }));

    for (recipe_level, level, craftsmanship, control, cp) in [
        (18, 18, 150, 140, 220), (50, 50, 350, 350, 350),
        (90, 90, 3400, 3300, 540), (100, 100, 4900, 4800, 620),
        (18, 100, 4900, 4800, 620), (90, 100, 4900, 4800, 620),
    ] {
        for requested_durability in [40, 0] {
            let (id, recipe) = RECIPES.entries().find(|(id, r)| {
                let rlvl = RLVLS[r.recipe_level as usize];
                !r.is_expert && r.max_level_scaling == 0 && rlvl.job_level == recipe_level
                    && (if requested_durability == 0 { rlvl.max_durability * r.durability_factor / 100 >= 60 }
                        else { (1..=40).contains(&(rlvl.max_durability * r.durability_factor / 100)) })
                    && r.quality_factor > 0
                    && raphael_data::RECIPE_TO_STELLAR_MISSION_LINKS.get(*id).is_none()
                    && r.req_craftsmanship <= craftsmanship && r.req_control <= control
                    && !raphael_data::ITEMS.get(r.item_id).is_some_and(|item| item.always_collectable)
            }).unwrap_or_else(|| panic!("No ordinary recipe category for recipe_level={recipe_level}, requested_durability={requested_durability}"));
            let rlvl = RLVLS[recipe.recipe_level as usize];
            let stats = CrafterStats { craftsmanship, control, cp, level, manipulation: level >= 65, heart_and_soul: false, quick_innovation: false };
            let settings = get_game_settings(*recipe, None, stats, None, None);
            let key = format!("recipe-{id}-level-{level}-durability-{}", settings.max_durability);
            // The core currently lacks live HQ/collectability metadata. Benchmark raw-quality
            // goals only; do not infer either capability from Raphael's sparse item table.
            let craft = json!({
                "RecipeId": id, "Craftsmanship": craftsmanship, "Control": control, "MaxCP": cp,
                "Level": level, "RecipeLevel": rlvl.job_level, "MaxDurability": settings.max_durability,
                "RequiredProgress": settings.max_progress, "MaxQuality": settings.max_quality, "RequiredQuality": 0,
                "ProgressDivider": rlvl.progress_div, "ProgressModifier": rlvl.progress_mod,
                "QualityDivider": rlvl.quality_div, "QualityModifier": rlvl.quality_mod,
                "ManipulationUnlocked": stats.manipulation,
            });
            let mut macro_settings = settings;
            // Solve the same full-quality objective used in the C# comparison.
            macro_settings.max_quality = settings.max_quality;
            write(&mut writer, json!({
                "Kind": "Recipe", "Key": key, "ItemId": recipe.item_id, "RecipeLevelRow": recipe.recipe_level,
                "Craft": craft, "BaseProgress": settings.base_progress, "BaseQuality": settings.base_quality,
                "InitialQuality": 0, "TargetQuality": settings.max_quality,
                "Raphael": macro_reference(macro_settings),
            }));

            let initial = SimulationState::new(&settings);
            let mut samples = vec![initial];
            let mut partial_start = initial;
            partial_start.quality = settings.max_quality / 4;
            samples.push(partial_start); // Includes Trained Eye filling, not adding, starting quality.
            // Replay deliberate quality, progress, repair, and combo setups through Rust only.
            for path in [
                vec![Action::BasicTouch, Action::StandardTouch],
                vec![Action::Reflect, Action::Innovation, Action::PreparatoryTouch],
                vec![Action::MuscleMemory, Action::Veneration],
                vec![Action::WasteNot, Action::BasicTouch, Action::MasterMend],
                vec![Action::Manipulation, Action::BasicTouch, Action::Observe],
                vec![Action::TrainedPerfection, Action::Groundwork],
            ] {
                let mut state = initial;
                for action in path {
                    match state.use_action(action, Condition::Normal, &settings) {
                        Ok(next) => { samples.push(next); state = next; }
                        Err(_) => break,
                    }
                }
            }
            // Boundary states are valid simulator inputs, not claims about the actual game's reader.
            let mut low = initial;
            low.effects.set_combo(Combo::None);
            low.cp = 18;
            low.durability = 5;
            samples.push(low);
            let mut iq = initial;
            iq.effects.set_combo(Combo::None);
            iq.effects.set_inner_quiet(10);
            iq.quality = settings.max_quality / 4;
            samples.push(iq);
            let mut near_finish = initial;
            near_finish.effects.set_combo(Combo::None);
            near_finish.progress = settings.max_progress - 1;
            near_finish.durability = 10;
            samples.push(near_finish);

            samples.sort_by_key(|s| (s.progress, s.quality, s.cp, s.durability, s.effects.into_bits()));
            samples.dedup();
            for state in samples {
                for condition in [Condition::Normal, Condition::Good, Condition::Excellent, Condition::Poor] {
                    for &action in ACTIONS {
                        let result = state.use_action(action, condition, &settings);
                        write(&mut writer, json!({
                            "Kind": "Transition", "RecipeKey": key,
                            "Before": state_json(state, condition, &settings), "ActionId": action.action_id(),
                            "Duration": action.time_cost(), "Legal": result.is_ok(),
                            "After": result.as_ref().ok().map(|s| state_json(*s, next_condition(condition), &settings)),
                            "Terminal": result.as_ref().ok().is_some_and(|s| s.is_final(&settings)),
                        }));
                    }
                }
            }
        }
    }
    writer.flush().unwrap();
}
