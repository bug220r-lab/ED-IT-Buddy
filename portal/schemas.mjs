import * as z from 'zod/v4';

const id = z.string().regex(/^[A-Za-z0-9_.-]{1,80}$/);
const clipId = z.string().regex(/^[A-Za-z0-9_-]{1,64}$/);
const path = z.string().min(4).max(240).regex(/^[A-Za-z]:[\\/]/).refine(value => !/[\0\r\n]/.test(value));
const frame = z.number().int().min(0).max(100000000);
const track = z.number().int().min(1).max(99);
const step = (action, args) => z.object({ action: z.literal(action), args: z.object(args).strict() }).strict();

export const stepSchema = z.discriminatedUnion('action', [
  step('create_project', { project_path: path, width: z.number().int().min(16).max(7680), height: z.number().int().min(16).max(4320), fps: z.number().int().min(1).max(240) }),
  step('import_media', { media_path: path, clip_id: clipId }),
  z.object({ action: z.literal('trim_clip'), args: z.object({ clip_id: clipId, source_in_frame: frame, source_out_frame: frame }).strict().refine(value => value.source_out_frame > value.source_in_frame, { message: 'Source out frame must be greater than source in frame.' }) }).strict(),
  step('append_clip', { clip_id: clipId, track }),
  step('add_title', { text: z.string().min(1).max(500), timeline_frame: frame, duration_frames: z.number().int().min(1).max(100000000), track }),
  step('save_project', { project_path: path }),
  step('export_video', { output_path: path })
]);

export const schemas = {
  get_state: z.object({}).strict(),
  get_capabilities: z.object({}).strict(),
  run_sequence: z.object({
    request_id: id.describe('Stable ID for this exact sequence. Reuse only to retrieve the existing submission, never to disguise a changed job.'),
    workspace: path.describe('Dedicated MSI workspace containing every project, input, and output path in this job.'),
    steps: z.array(stepSchema).min(1).max(64)
  }).strict(),
  get_job: z.object({ job_id: id }).strict(),
  stop_job: z.object({ job_id: id }).strict()
};

export const descriptions = {
  get_state: 'Read MSI desktop readiness, MAGIX identity, active dialog and current Buddy job. Does not focus or edit an application.',
  get_capabilities: 'List editing actions with their verified support and evidence for the current MAGIX build. Unsupported actions must not be assumed to work.',
  run_sequence: 'Run an authorized typed sequence in MSI MAGIX. All paths must be inside its dedicated workspace. Frames are zero-based, trim out is exclusive, tracks are one-based. Requires a unique stable request_id. Ambiguous state stops the job. After uncertain transport failure, query the job; never blindly replay.',
  get_job: 'Read a submitted job, per-step results, verification evidence and output paths. May reconnect after SSH loss without replaying any editing actions.',
  stop_job: 'Request that no further steps start for this job. Does not undo completed edits or close MAGIX; an already-running native operation or export may continue.'
};
