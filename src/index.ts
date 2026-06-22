import express from 'express';
import config from './config';

let app = require('./server').default;

if (module.hot) {
    module.hot.accept('./server', () => {
        console.log('🔁  HMR Reloading `./server`...');
        try {
            app = require('./server').default;
        } catch (error) {
            console.error(error);
        }
    });
    console.info('✅  Server-side HMR Enabled!');
}

const port = process.env.API_PORT ? parseInt(process.env.API_PORT, 10) : 4000;

const server = express()
    .use((req, res) => app.handle(req, res))
    .listen(port, (err: Error) => {
        if (err) {
            console.error(err);
            return;
        }
        console.log(`> Started on port ${port}`);
        // Surface the effective account-create captcha enforcement, so a stray CAPTCHA_MODE
        // (anything but "off" is now "hard") shows up in startup logs instead of being silent.
        console.log(
            `> account-create captcha: ${config.captchaMode === 'off' ? 'OFF (break-glass)' : 'hard'}`
        );
    });

['SIGINT', 'SIGTERM'].forEach((signal: any) => {
    process.on(signal, () => {
        console.info(`Shutting down because of ${signal}`);
        server.close(() => {
            console.error('Server closed gracefully')
        });
    })
});

export default server;
