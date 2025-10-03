# [Ecency vision][ecency_vision] – API

### Build instructions

##### Requirements

- node ^12.0.0
- yarn

##### Clone 
`$ git clone https://github.com/ecency/vision-api`

`$ cd vision-api`

##### Install dependencies
`$ yarn`

##### Edit config file or define environment variables
`$ nano src/config.ts`

##### Environment variables

* `PRIVATE_API_ADDR` - private api endpoint
* `PRIVATE_API_AUTH` - private api auth
* `HIVESIGNER_SECRET` -  hivesigner client secret
* `SEARCH_API_ADDR` - hivesearcher api endpoint
* `SEARCH_API_SECRET` - hivesearcher api auth token
* `CHAINSTACK_API_KEY` - Chainstack API key used to enumerate nodes and request balances directly from each network-specific endpoint.
* `CHAINZ_API_KEY` - Chainz API key used to perform balance lookups when the Chainz provider is requested.

##### Start api in dev
`$ yarn start`

##### Pushing new code / Pull requests

- Make sure to branch off your changes from `main` branch.
- Make sure to run `yarn test` and add tests to your changes.
- Code on!

## Docker

You can use official `ecency/api:latest` image to run Vision locally, deploy it to staging or even production environment. The simplest way is to run it with following command:

```bash
docker run -it --rm -p 3000:3000 ecency/vision:latest
```

Configure the instance using following environment variables:

 * `PRIVATE_API_ADDR`
 * `PRIVATE_API_AUTH`
 * `HIVESIGNER_SECRET`
 * `SEARCH_API_ADDR`
 * `SEARCH_API_SECRET`
 * `CHAINSTACK_API_KEY`
 * `CHAINZ_API_KEY`

```bash
docker run -it --rm -p 3000:3000 -e PRIVATE_API_ADDR=https://api.example.com -e PRIVATE_API_AUTH=verysecretpassword ecency/api:latest
```

### Swarm

You can easily deploy a set of vision instances to your production environment, using example `docker-compose.yml` file. Docker Swarm will automatically keep it alive and load balance incoming traffic between the containers:

```bash
docker stack deploy -c docker-compose.yml vision-api
```

## Issues

To report a non-critical issue, please file an issue on this GitHub project.

If you find a security issue please report details to: security@ecency.com

We will evaluate the risk and make a patch available before filing the issue.

[//]: # 'LINKS'
[ecency_vision]: https://ecency.com
