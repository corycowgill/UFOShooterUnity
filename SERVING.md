# Serving this build

The player is Brotli-compressed. Because `decompressionFallback` is enabled, Unity emits the
payload as `.unityweb` files and decompresses them in JavaScript, so **this build runs on any
static host with no special headers**. That is the safe default and why it is on.

It is not the fast default. Decompressing in JS costs a second or two of startup. To let the
browser do it natively instead:

1. Set `PlayerSettings.WebGL.decompressionFallback = false` in ProjectSetup.
2. Rebuild - Unity then emits `.br` files.
3. Serve them with the original content type plus:

       Content-Encoding: br

   (`application/wasm` for the wasm, `application/javascript` for the framework.)

Getting step 3 wrong is the classic "blank canvas" failure, which is exactly why the fallback
is on until someone opts into the faster path.

## Caching

`Build/*` is marked `immutable` in the template, so a returning player reads the payload out of
IndexedDB and transfers nothing at all. The only thing that can invalidate that copy is the
product version, which `Tools/build-web.sh` derives from the commit and passes as
`-buildVersion`. Build through that script, or through anything else that passes the flag - a
build that ships new content under an old version is one that returning players never see.

`index.html` is deliberately excluded from this: it must stay revalidated, because it is what
carries the new version to the browser in the first place.

Local check, either way:

    node Tools/serve.mjs --dir Build/web --port 8123
