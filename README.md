# GTPSPVolTools

Unpacks and packs volume files (GT.VOL) for Gran Turismo PSP.

Refer to the [Modding Hub](https://nenkai.github.io/gt-modding-hub/psp/getting_started/).

> [!NOTE]
> Note when packing:
> 
> The original volume orders file **data** on the volume sequentially mainly by *game file load order*, so for example, the presents textures are written first, then the core scripts, then the specdb, etc. This is mainly done to reduce the amount of seeking the umd reader has to do on the disc for performance reasons.
> 
> GTPSPVolTools does not write the data by this order, but simply by the original file tree order. **Therefore expect load times to be potentially very slightly slower on real hardware.**
