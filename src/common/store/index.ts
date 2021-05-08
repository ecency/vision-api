import {combineReducers} from "redux";
import {connectRouter} from "connected-react-router";
import {createBrowserHistory, createMemoryHistory, History} from "history";


import isEqual from "react-fast-compare";

import global from "./global";
import dynamicProps from "./dynamic-props";
import trendingTags from "./trending-tags";
import entries from "./entries";
import accounts from "./accounts";
import communities from "./communities";
import transactions from "./transactions";
import users from "./users";
import activeUser from "./active-user";
import reblogs from "./reblogs";
import discussion from "./discussion";
import ui from "./ui";
import subscriptions from "./subscriptions";
import notifications from "./notifications";
import points from "./points";
import signingKey from "./signing-key";
import entryPinTracker from "./entry-pin-tracker";

import filterTagExtract from "../helper/filter-tag-extract";

let reducers = { };

export let history: History | undefined;

// create browser history on client side
if (typeof window !== "undefined") {
    // @ts-ignore
    reducers = {router: connectRouter(history), ...reducers};
}

const rootReducer = combineReducers(reducers);

export default rootReducer;

export type AppState = ReturnType<typeof rootReducer>;
