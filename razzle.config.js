'use strict';

module.exports = {
  plugins: ['typescript'],
  modify: (config, { target, dev }) => {
    config.devtool = dev ? 'source-map' : false;
    return config;
  },
};
