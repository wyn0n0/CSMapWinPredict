namespace CsDemoMap.Cli;

internal static class DeveloperCommandHelp
{
    public const string Text = """
        Usage: npm run cli -- <command> <arguments>
        Help: npm run cli -- --help
        Run from the repository root. Quote paths containing spaces.

        Demo and win data:
          --inspect-demo <demo.dem>
          --trace-roster <demo.dem> <from-tick> <to-tick>
          --export-win-data <demo-directory> <new-output>
          --export-win-data-v4 <demo-directory> <new-output>
          --export-win-spatial <demo-or-directory> <new-output> <support-radius> <site-radius>
            Experimental win-spatial-v1 companion; radii in world units, selected on train/dev only.

        Situation samples and acceptance:
          --export-situation-samples <request.json> <new-output>
          --rebuild-situation-sidecars <import-directory> <demo.dem> <new-output>
          --run-situation-stage-two-acceptance <request.json> <new-output>
          --run-situation-stage-three-calibration <request.json> <new-output>
          --run-situation-stage-three-acceptance <request.json> <new-output>

        Dataset export:
          --create-situation-stage-four-split <demos> <parent-split> <new-split>
          --export-situation-training-data <demos> <split> <new-output> --pilot <limit>
          --export-situation-training-data <demos> <split> <output> [--resume] [--round-workers <n>] [--train-benchmark]
          --export-situation-tokenizer-inputs <pilot> <new-output>
          --export-situation-stage6-source <demos> <split> <base> <new-output> <partition> <purpose>
          --export-situation-stage6-source-shard <demos> <split> <base> <new-output> <partition> <purpose> <shard-index> <shard-count>

        Raycast and derived candidates:
          --analyze-situation-raycast <scene.json> <new-output>
          --sample-situation-raycast <dataset> <new-output>
          --sample-situation-local-peek <dataset> <new-output>
          --sample-situation-position-prediction <dataset> <new-output>
          --sample-situation-continuous-slope <dataset> <new-output>
          --sample-situation-local-peek-3s <dataset> <new-output>
          --sample-situation-verified-exposure <dataset> <new-output>
          --sample-situation-close-exposure <dataset> <new-output>

        Human review:
          --export-situation-review-candidates <dataset> <new-output>
          --verify-situation-review-candidates <dataset> <candidates>
          --verify-situation-review-consumer <dataset> <candidates> <producer-snapshot>
          --serve-situation-review <dataset> <work> --candidates <candidates> [options]
          --freeze-situation-review <dataset> <work> <new-output> [options]
          --verify-situation-review <dataset> <output> [options]
        Review options:
          --candidates <directory> --producer-snapshot <directory>
          Choose at most one: --raycast-candidates, --local-peek-candidates,
            --position-prediction-candidates, --continuous-slope-candidates,
            --verified-exposure-candidates, --close-exposure-candidates (each takes a directory).
          Serve only: --review-target 50 --port <0..65535>
          Freeze only: --approved-content-sha256 <approved-digest>
        Approval workflow: docs/context/RUNBOOK.md and the linked review reports.

        Outputs must be new unless using an explicitly supported resume workflow.
        Test entry: npm run test:groups -- --help
        """;
}
