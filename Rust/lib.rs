// ── text_effects_rs ──
// Native Rust compute library for the Saitama mod's text effects.
// Exports C-ABI functions callable from Unity C# via DllImport.
//
// Three compute kernels ported from Unity IJobParallelFor:
// 1. batch_srgb_to_oklab    — sRGB → Oklab color space conversion
// 2. build_static_wrap_table — Gaussian-smoothed oversampled LUT
// 3. apply_one_group         — per-vertex animated color (hot path, every frame)

mod color_apply;
mod oklab;
mod wrap_table;

use std::ffi::c_float;

// ═══════════════════════════════════════════════════════════════
// FFI: batch_srgb_to_oklab
// ═══════════════════════════════════════════════════════════════

/// Batch-convert sRGB colors to Oklab.
///
/// # Safety
/// All pointers must be valid for `count` elements.
/// `l`, `a`, `b_ok` must be writable.
#[no_mangle]
pub unsafe extern "C" fn rs_batch_srgb_to_oklab(
    r: *const c_float,
    g: *const c_float,
    b: *const c_float,
    l: *mut c_float,
    a: *mut c_float,
    b_ok: *mut c_float,
    count: i32,
) {
    if count <= 0 {
        return;
    }
    let n = count as usize;
    let r_slice = std::slice::from_raw_parts(r, n);
    let g_slice = std::slice::from_raw_parts(g, n);
    let b_in = std::slice::from_raw_parts(b, n);
    let l_slice = std::slice::from_raw_parts_mut(l, n);
    let a_slice = std::slice::from_raw_parts_mut(a, n);
    let b_out = std::slice::from_raw_parts_mut(b_ok, n);

    oklab::batch_srgb_to_oklab(r_slice, g_slice, b_in, l_slice, a_slice, b_out);
}

// ═══════════════════════════════════════════════════════════════
// FFI: build_static_wrap_table
// ═══════════════════════════════════════════════════════════════

/// Build a Gaussian-smoothed, oversampled static wrap LUT from a ring buffer.
///
/// # Safety
/// All pointers must be valid for their respective lengths.
#[no_mangle]
pub unsafe extern "C" fn rs_build_static_wrap_table(
    ring_l: *const c_float,
    ring_a: *const c_float,
    ring_b: *const c_float,
    kernel_weights: *const c_float,
    window_ring_len: i32,
    ring_len: i32,
    kernel_radius: i32,
    smooth_len: i32,
    inv_oversample: f64,
    inv_sigma2x2: f64,
    out_l: *mut c_float,
    out_a: *mut c_float,
    out_b: *mut c_float,
) {
    let wrl = window_ring_len.max(1) as usize;
    let rl = ring_len.max(1) as usize;
    let kr = kernel_radius.max(0) as usize;
    let sl = smooth_len.max(1) as usize;

    let ring_l = std::slice::from_raw_parts(ring_l, rl);
    let ring_a = std::slice::from_raw_parts(ring_a, rl);
    let ring_b = std::slice::from_raw_parts(ring_b, rl);
    let kw = std::slice::from_raw_parts(kernel_weights, kr * 2 + 1);
    let out_l = std::slice::from_raw_parts_mut(out_l, sl);
    let out_a = std::slice::from_raw_parts_mut(out_a, sl);
    let out_b = std::slice::from_raw_parts_mut(out_b, sl);

    wrap_table::build_static_wrap_table(
        ring_l, ring_a, ring_b,
        kw,
        wrl, rl, kr, sl,
        inv_oversample, inv_sigma2x2,
        out_l, out_a, out_b,
    );
}

// ═══════════════════════════════════════════════════════════════
// FFI: apply_one_group — the per-frame hot path
// ═══════════════════════════════════════════════════════════════

/// Apply animated vertical-line colors to one mesh group's vertices.
///
/// Each of `count` effect-vertices maps to a position in the `colors` output
/// array (via `vert_index`). Config parameters are indexed by `config_idx`.
///
/// # Safety
/// - `static_wrap_l/a/b` must be valid for reads up to
///   max(config_wrap_starts + config_smooth_lens) across all configs.
/// - `vert_index`, `normalized_pos`, `config_idx` must be valid for `count`.
/// - `config_*` arrays must be valid for `num_configs`.
/// - `colors` must be valid for at least (max(vert_index) + 1) * 4 bytes.
/// - `count` and `num_configs` must be accurate.
#[no_mangle]
pub unsafe extern "C" fn rs_apply_one_group(
    static_wrap_l: *const c_float,
    static_wrap_a: *const c_float,
    static_wrap_b: *const c_float,
    vert_index: *const i32,
    normalized_pos: *const c_float,
    config_idx: *const i32,
    config_wrap_starts: *const i32,
    config_smooth_lens: *const i32,
    config_masks: *const i32,
    config_shift_ints: *const i32,
    config_shift_fracs: *const c_float,
    config_cycle_comps: *const c_float,
    num_configs: i32,
    colors: *mut u8,
    count: i32,
) {
    let nc = num_configs.max(0) as usize;
    let n = count.max(0) as usize;
    if n == 0 || nc == 0 {
        return;
    }

    color_apply::apply_one_group(
        static_wrap_l,
        static_wrap_a,
        static_wrap_b,
        vert_index,
        normalized_pos,
        config_idx,
        config_wrap_starts,
        config_smooth_lens,
        config_masks,
        config_shift_ints,
        config_shift_fracs,
        config_cycle_comps,
        nc,
        colors,
        n,
    );
}

// ═══════════════════════════════════════════════════════════════
// FFI: apply_all_groups — batched, single P/Invoke, all mesh groups
// ═══════════════════════════════════════════════════════════════

#[no_mangle]
pub unsafe extern "C" fn rs_apply_all_groups(
    static_wrap_l: *const c_float,
    static_wrap_a: *const c_float,
    static_wrap_b: *const c_float,
    vert_index: *const i32,
    normalized_pos: *const c_float,
    config_idx: *const i32,
    config_wrap_starts: *const i32,
    config_smooth_lens: *const i32,
    config_masks: *const i32,
    config_shift_ints: *const i32,
    config_shift_fracs: *const c_float,
    config_cycle_comps: *const c_float,
    num_configs: i32,
    groups: *const color_apply::MeshGroupHeader,
    num_groups: i32,
) {
    let nc = num_configs.max(0) as usize;
    let ng = num_groups.max(0) as usize;
    if ng == 0 || nc == 0 {
        return;
    }

    color_apply::apply_all_groups(
        static_wrap_l,
        static_wrap_a,
        static_wrap_b,
        vert_index,
        normalized_pos,
        config_idx,
        config_wrap_starts,
        config_smooth_lens,
        config_masks,
        config_shift_ints,
        config_shift_fracs,
        config_cycle_comps,
        nc,
        groups,
        ng,
    );
}

// ═══════════════════════════════════════════════════════════════
// FFI: batch_oklab_to_rgba32 (utility for bulk Oklab→sRGB)
// ═══════════════════════════════════════════════════════════════

/// Batch-convert Oklab values to packed RGBA32 bytes.
///
/// # Safety
/// All pointers must be valid for `count` elements (`rgba` for `count * 4` bytes).
#[no_mangle]
pub unsafe extern "C" fn rs_batch_oklab_to_rgba32(
    l: *const c_float,
    a: *const c_float,
    b_ok: *const c_float,
    rgba: *mut u8,
    count: i32,
) {
    let n = count.max(0) as usize;
    if n == 0 {
        return;
    }
    let l_slice = std::slice::from_raw_parts(l, n);
    let a_slice = std::slice::from_raw_parts(a, n);
    let b_slice = std::slice::from_raw_parts(b_ok, n);
    let rgba_slice = std::slice::from_raw_parts_mut(rgba, n * 4);

    oklab::batch_oklab_to_rgba32(l_slice, a_slice, b_slice, rgba_slice);
}
