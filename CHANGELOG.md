# Changelog

## 0.1.0 (2026-11-10)


### Features

* add feature flag seam and password-less connection strings ([cf918dc](https://github.com/billigmeier-labs/orders-demo/commit/cf918dcd6f733a6221e9c64dd8a661f83099a01e))
* add GET /api/products/low-stock endpoint ([#32](https://github.com/billigmeier-labs/orders-demo/issues/32)) ([c7a5f79](https://github.com/billigmeier-labs/orders-demo/commit/c7a5f79c6c4ce2fd543997eba268ad6e22d35edd))
* add GET /api/products/low-stock endpoint ([#35](https://github.com/billigmeier-labs/orders-demo/issues/35)) ([e167885](https://github.com/billigmeier-labs/orders-demo/commit/e1678850ca503f00eb9603dfb2130164e74c6480))
* add low-stock products endpoint ([#25](https://github.com/billigmeier-labs/orders-demo/issues/25)) ([83c0ba5](https://github.com/billigmeier-labs/orders-demo/commit/83c0ba5118049f35e3a2058520d80f2e3ea88f96))
* add low-stock products endpoint ([#38](https://github.com/billigmeier-labs/orders-demo/issues/38)) ([d5f830d](https://github.com/billigmeier-labs/orders-demo/commit/d5f830d4acd3655116be2ae6e885a2bfc0dc79ac))
* add low-stock products endpoint ([#5](https://github.com/billigmeier-labs/orders-demo/issues/5)) ([681211b](https://github.com/billigmeier-labs/orders-demo/commit/681211b207c9dbc15d6831c26eed851a4040033c))
* add multi-arch API image with EF migration bundle ([fb7c896](https://github.com/billigmeier-labs/orders-demo/commit/fb7c896be96d698be098c754045c63885035d6fe))
* add orders API with EF Core and PostgreSQL ([cd71839](https://github.com/billigmeier-labs/orders-demo/commit/cd7183975b8378023f68a0827a8b3f3d59d4cfae))
* **api:** AppConfig feature flags, fault injection, search v2 and structured logs ([fc1978a](https://github.com/billigmeier-labs/orders-demo/commit/fc1978a2ec61e7340e6ef2739aa65fd8506da8a1))
* **ci:** nightly mutation testing that files issues for the weakest files ([ef87e0b](https://github.com/billigmeier-labs/orders-demo/commit/ef87e0b73705b0caa3ce233a414bc551d7f8ed92))
* **ci:** workflow automation acts as the orders-automation GitHub App instead of a personal token ([5c1d091](https://github.com/billigmeier-labs/orders-demo/commit/5c1d0914242fc326a5a09b334f04aa5eb773dc23))
* **console:** bad-deploy shows the AI review and the admin override as steps ([7c487f9](https://github.com/billigmeier-labs/orders-demo/commit/7c487f9c66a71a03d2926e840d0eb3afdc497b7c))
* **console:** show the deployed commit in Recent deploys ([0a514ce](https://github.com/billigmeier-labs/orders-demo/commit/0a514ce2340c74142e9efe48d16024c397244d8e))
* **console:** summarize production errors on one line ([7549713](https://github.com/billigmeier-labs/orders-demo/commit/7549713c29cf8151cbdd87494b19a0ef534cf71d))
* **console:** the mutation scenario shows recent weak spots and the agent PRs that fixed them, closed ones included ([146329f](https://github.com/billigmeier-labs/orders-demo/commit/146329f0c5c82103c668e7e37fac95684e39dbd0))
* demo console with Status, Present and Architecture modes ([f4b1933](https://github.com/billigmeier-labs/orders-demo/commit/f4b193385791064020590fee329bb9da37177317))
* **demo:** demo reset recipes; faster rollback with a 5-minute bake and a 20-connection pool ([ac96360](https://github.com/billigmeier-labs/orders-demo/commit/ac96360f599fee83e30e1a2e59bcc2e5b7ee19a4))
* **demo:** just demo flag &lt;name&gt; on|off [env] ([ceb2772](https://github.com/billigmeier-labs/orders-demo/commit/ceb277267134128773ccfebc0893358cccaf0e5a))
* **infra:** add AWS Terraform base and env layers ([49cdafd](https://github.com/billigmeier-labs/orders-demo/commit/49cdafd0ed9b65657e9c37a6e904cf9eb8eb43cb))
* **infra:** ADOT collector sidecar sends API traces to X-Ray ([0558efc](https://github.com/billigmeier-labs/orders-demo/commit/0558efc9190d6e660da7c71705e1c06f687c9b1d))
* **infra:** wire the console service, instant AppConfig strategy and preview role access ([495c5e4](https://github.com/billigmeier-labs/orders-demo/commit/495c5e47d6d89b586d204c4203152f624d7a3630))
* list a customer's orders ([#10](https://github.com/billigmeier-labs/orders-demo/issues/10)) ([c6f0c3c](https://github.com/billigmeier-labs/orders-demo/commit/c6f0c3cd90348f9118dd8adede3f822a0e92aa58))
* **loadgen:** NBomber load generator with normal, peak, spike and soak ([6e94f66](https://github.com/billigmeier-labs/orders-demo/commit/6e94f663f40e7da3698b6749951dac364e7eaab9))
* **observability:** send API traces to X-Ray via a collector sidecar, previews included ([3e2cfcc](https://github.com/billigmeier-labs/orders-demo/commit/3e2cfcc06aa332f17d977e81fbadc4bad395627c))
* restock products so load-test orders keep succeeding ([#54](https://github.com/billigmeier-labs/orders-demo/issues/54)) ([e34fe7e](https://github.com/billigmeier-labs/orders-demo/commit/e34fe7e98902faf5aaf08cd40643026a9d888684))


### Bug Fixes

* **agent:** auto-merge step reads the agent's output safely ([2ed933b](https://github.com/billigmeier-labs/orders-demo/commit/2ed933b866e2226da83cf3542220f17099157fb4))
* **api:** readable JSON log escaping; start the test API before deriving hosts ([da96780](https://github.com/billigmeier-labs/orders-demo/commit/da96780221b230e22729b141ca173d2d9f43c723))
* build a valid tsquery for multi-word search terms ([#28](https://github.com/billigmeier-labs/orders-demo/issues/28)) ([08478d0](https://github.com/billigmeier-labs/orders-demo/commit/08478d0b0794c08f8d284a6beb05e358c3e02d0f))
* **ci:** fall back to another just installer when GitHub downloads fail; guard agent auto-merge ([3044931](https://github.com/billigmeier-labs/orders-demo/commit/30449313fce0528992d98242251c46b8472d27ce))
* **ci:** let the orders-automation app start Claude runs ([49c7b5a](https://github.com/billigmeier-labs/orders-demo/commit/49c7b5a385616312201aca7852d70039fa94b8d0))
* **ci:** stop gitleaks flagging a commit SHA in a console test ([ff420cc](https://github.com/billigmeier-labs/orders-demo/commit/ff420cc181b2c39d9cf8e9999962bc3ade08650a))
* **console:** deploy steps wait for the deploy of their own PR, not any deploy ([f131cd8](https://github.com/billigmeier-labs/orders-demo/commit/f131cd806d9b4850638db4b1f884a9720f236e06))
* **console:** give the bad-deploy alarm step time for staging to deploy first ([58b9987](https://github.com/billigmeier-labs/orders-demo/commit/58b9987c8f3a310d9788acdc6bb131d0c2a42d07))
* **console:** scoreboard counts AI-review blocks and real prod rollbacks ([db9c196](https://github.com/billigmeier-labs/orders-demo/commit/db9c196c33ff4568d988354eaeb1026445e13408))
* **console:** show real feature flag descriptions ([e4cf062](https://github.com/billigmeier-labs/orders-demo/commit/e4cf06253a868572940dc1482c5ad3ff77893164))
* **console:** show the extended wait budget after Keep waiting ([d5425e1](https://github.com/billigmeier-labs/orders-demo/commit/d5425e126c810f710ed1e83b5a32c752b2f2061e))
* **console:** tag load generator tasks component=loadgen ([43ff7fa](https://github.com/billigmeier-labs/orders-demo/commit/43ff7fa772f16eb1a5190c506d94a4e09dbece58))
* **console:** tag load generator tasks project=orders-demo ([face5da](https://github.com/billigmeier-labs/orders-demo/commit/face5da72920beadc16551ece44151135d1b662f))
* **console:** the header's step counter follows the run ([8f238b5](https://github.com/billigmeier-labs/orders-demo/commit/8f238b583a9fb7facad2d13893d8357bddbc801c))
* **console:** the incident scenario uses the searchV2 bug, not a fault flag ([da3d392](https://github.com/billigmeier-labs/orders-demo/commit/da3d3923288bddfb13b1ae5218c11154469bf269))
* **console:** the Tour keeps going when the AI review blocks once; fails only if a person is needed ([908c62b](https://github.com/billigmeier-labs/orders-demo/commit/908c62bb9d385a313a0da150caff5bf909b26806))
* **demo:** avoid SIGPIPE in reset recipes ([ca9b096](https://github.com/billigmeier-labs/orders-demo/commit/ca9b09683fa8409dbab5cb92a84c6e6dee6d9af2))
* **demo:** load goes to production, and lasts long enough for the bad-deploy scenario ([fc9c4bc](https://github.com/billigmeier-labs/orders-demo/commit/fc9c4bcaff1cd8304f8db68da3c94a0f4e24b063))
* **demo:** rebuild the canned bug with a 3-way apply from its latest version ([43443bb](https://github.com/billigmeier-labs/orders-demo/commit/43443bb4ca9ff66936abb15d319676a73d679ed3))
* **demo:** rebuild the canned-bug branch after main is reset; retry container pull timeouts ([983a814](https://github.com/billigmeier-labs/orders-demo/commit/983a814cd11324b221d1a3b271c4c7daafeddee1))
* **demo:** reset the Tour by reverting the agent's low-stock endpoint ([ddf14f1](https://github.com/billigmeier-labs/orders-demo/commit/ddf14f1374e75a134b85bbb5852971d38e1b7ecd))
* **demo:** resets decide from the code, need a clean checkout, and never hard-reset ([7bf9b57](https://github.com/billigmeier-labs/orders-demo/commit/7bf9b577bcb76213e455de332fd43d8baed981ec))
* **demo:** the risky-change scenario asks for a genuinely new column; AGENTS.md says new migrations are allowed ([a485eb9](https://github.com/billigmeier-labs/orders-demo/commit/a485eb96318100c570f76b746825cd89e3909a6d))
* **deploy:** only redeploy the console when its code changed ([a10aa25](https://github.com/billigmeier-labs/orders-demo/commit/a10aa25ea9a62124ad9d77cd8ac104e89a5ac034))
* **incident:** post recovery on the latest incident even after the fix closed it ([aaca240](https://github.com/billigmeier-labs/orders-demo/commit/aaca24052c7fb6676cbde981d4568313ec5c27e8))
* **infra:** 1-minute staging bake, console may tag loadgen tasks ([bc40327](https://github.com/billigmeier-labs/orders-demo/commit/bc403278a137543333888cd19ab565f111daf492))
* **infra:** isolate PR previews from production and console secrets ([69bf06e](https://github.com/billigmeier-labs/orders-demo/commit/69bf06e4d3ec504f854defa6ddea68b620fd60bf))
* **infra:** trust GitHub environments for deploy roles, separate preview ECR repo ([cca063c](https://github.com/billigmeier-labs/orders-demo/commit/cca063cdaa3de04406497b7930bc5b9f11bb3a83))
* **infra:** trust GitHub's immutable OIDC subject and stop flag drift ([a855565](https://github.com/billigmeier-labs/orders-demo/commit/a8555652147d549c9a2a72ebb0df4ac928c8bca3))
* make search LIKE escaping work and cover search with tests ([#47](https://github.com/billigmeier-labs/orders-demo/issues/47)) ([c5a1389](https://github.com/billigmeier-labs/orders-demo/commit/c5a1389c2008a1fd0e92a49e7b735a8f374e534c))
* **search:** support multi-word terms in searchV2 ([#15](https://github.com/billigmeier-labs/orders-demo/issues/15)) ([08b14ff](https://github.com/billigmeier-labs/orders-demo/commit/08b14ff8569919b43ea332123d81d0f1da18624e))
* searchV2 multi-word queries no longer fail with tsquery syntax error ([#43](https://github.com/billigmeier-labs/orders-demo/issues/43)) ([4ad5d3a](https://github.com/billigmeier-labs/orders-demo/commit/4ad5d3a71629074051774470068b7bc362423253))
* triage our own bots' issues; demo reset closes leftover incident issues ([192df10](https://github.com/billigmeier-labs/orders-demo/commit/192df10e05c51e683afb602e0129283afe2d87ea))


### Performance

* cache product prices ([#18](https://github.com/billigmeier-labs/orders-demo/issues/18)) ([03fe335](https://github.com/billigmeier-labs/orders-demo/commit/03fe3352d279e0c4e70d8a776a41534a1b93675d))
* cache product prices ([#21](https://github.com/billigmeier-labs/orders-demo/issues/21)) ([ae550a6](https://github.com/billigmeier-labs/orders-demo/commit/ae550a6210311288384082c3f84bff5630bef34c))
* cache product prices ([#22](https://github.com/billigmeier-labs/orders-demo/issues/22)) ([c373b5b](https://github.com/billigmeier-labs/orders-demo/commit/c373b5bca6940badbc0d6e845758c0b80be8fc95))
* cache product prices ([#39](https://github.com/billigmeier-labs/orders-demo/issues/39)) ([2110ae3](https://github.com/billigmeier-labs/orders-demo/commit/2110ae38436fcebc3754b51ec3055e72d8af8143))
* **search:** run the product and order lookups in parallel ([#12](https://github.com/billigmeier-labs/orders-demo/issues/12)) ([bce57d1](https://github.com/billigmeier-labs/orders-demo/commit/bce57d169e4b9db7e29a0bfcc4a101323a3ce32b))


### Reverts

* **demo:** remove the canned bug after a bad-deploy demo ([a821e39](https://github.com/billigmeier-labs/orders-demo/commit/a821e395da0b9600fca5967d920e73f1eb9e8356))
* **demo:** remove the canned bug after a bad-deploy demo ([6a3e869](https://github.com/billigmeier-labs/orders-demo/commit/6a3e8697f12b4cc3494992d968dcca867fad08a6))
* **demo:** remove the canned bug after a bad-deploy demo ([19326ee](https://github.com/billigmeier-labs/orders-demo/commit/19326eeb192cfbf4bf52b100f86ab0a0e05f6492))
* **demo:** remove the canned bug after a bad-deploy demo ([808274f](https://github.com/billigmeier-labs/orders-demo/commit/808274f300a23fd25d93ebb29824653bfeb452ff))


### Documentation

* add README ([c66b07f](https://github.com/billigmeier-labs/orders-demo/commit/c66b07f2cc6f192ea005a6f16bc06779d7496a72))
* add the demo console design spike ([1d51d4c](https://github.com/billigmeier-labs/orders-demo/commit/1d51d4cdbc0103b2b77f56f9e14f52139b3e4cdb))
* **console:** bad-deploy narration says only an admin's override counts ([311222c](https://github.com/billigmeier-labs/orders-demo/commit/311222c9f7253d956f16fa6c4cd1e4819f1808e8))
* **console:** make Architecture and Tour text match what is built ([a9602cc](https://github.com/billigmeier-labs/orders-demo/commit/a9602cc1d8a86ceadbd1d49a997dace6bb6caff3))
* contracts, agent rules and decision log ([be87ae9](https://github.com/billigmeier-labs/orders-demo/commit/be87ae95c54e84ab5e92b36934f7449632e157a5))
