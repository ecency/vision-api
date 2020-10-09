import React, {Component} from "react";

import {Link} from "react-router-dom";

import {History, Location} from "history";

import {Button} from "react-bootstrap";

import isEqual from "react-fast-compare";

import {pathToRegexp} from "path-to-regexp";

import {Global, Theme} from "../../store/global/types";
import {TrendingTags} from "../../store/trending-tags/types";
import {Account} from "../../store/accounts/types";
import {User} from "../../store/users/types";
import {ActiveUser} from "../../store/active-user/types";
import {UI, ToggleType} from "../../store/ui/types";
import {NotificationFilter, Notifications} from "../../store/notifications/types";

import ToolTip from "../tooltip";
import Login from "../login";
import UserNav from "../user-nav";
import SignUp from "../sign-up";

import NotificationHandler from "../notification-handler";

import {_t} from "../../i18n";

import _c from "../../util/fix-class-names";

import defaults from "../../constants/defaults.json";

import routes from "../../routes";

import {brightnessSvg, pencilOutlineSvg, arrowLeftSvg, arrowRightSvg, refreshSvg, magnifySvg} from "../../img/svg";

const logo = require('../../img/logo-circle.svg');

interface AddressBarProps {
    history: History;
    location: Location;
}

interface AddressBarState {
    address: string;
    realAddress: string;
    changed: boolean;
}


export class AddressBar extends Component<AddressBarProps, AddressBarState> {
    state: AddressBarState = {
        address: '',
        realAddress: '',
        changed: false
    }

    componentDidMount() {
        this.fixAddress();
    }

    shouldComponentUpdate(nextProps: Readonly<AddressBarProps>, nextState: Readonly<AddressBarState>) {
        return !isEqual(this.state, nextState);
    }

    componentDidUpdate(prevProps: Readonly<AddressBarProps>) {
        const {location} = this.props;

        if (location !== prevProps.location) {
            this.fixAddress();
        }
    }

    fixAddress = () => {
        const {history} = this.props;

        // @ts-ignore
        const {entries, index} = history;

        const curPath = entries[index].pathname;
        const address = curPath === '/' ? `${defaults.filter}` : curPath.replace('/', '');

        /* persist search string
        let q = '';
        if (location.pathname.startsWith('/search')) {
            const qs = queryString.parse(location.search);
            if (qs.q && typeof qs.q === "string") {
                ({q} = qs);
            }
        }*/

        this.setState({address, realAddress: address});
    };

    addressChanged = (e: React.ChangeEvent<HTMLInputElement>) => {
        this.setState({
            address: e.target.value,
            changed: true
        });
    };

    addressKeyup = async (e: React.KeyboardEvent<HTMLInputElement>) => {
        if (e.keyCode === 13) {
            const {address, changed} = this.state;
            const {history} = this.props;

            if (!changed) return;

            if (address.trim() === '') {
                return;
            }

            const url = new URL(address, 'https://ecency.com');
            const pathMatch = !!Object.values(routes).find(p => {
                return pathToRegexp(p).test(url.pathname)
            });

            if (pathMatch) {
                history.push(url.pathname);
                return;
            }

            console.log("search");
            // history.push(`/search?q=${encodeURIComponent(q)}&sort=${searchSort}`);
        }

        if (e.keyCode === 27) {
            const {realAddress} = this.state;

            this.setState({address: realAddress});
        }
    };


    render() {
        const {address} = this.state;

        return (
            <div className="address">
                <div className="pre-add-on">{magnifySvg}</div>
                <span className="protocol">ecency://</span>
                <input
                    className="url"
                    value={address}
                    onChange={this.addressChanged}
                    onKeyUp={this.addressKeyup}
                    placeholder={_t('navbar.address-placeholder')}
                    spellCheck={false}
                />
            </div>
        )
    }
}

interface NavControlsProps {
    history: History;
    reloadFn: () => any,
    reloading: boolean,
}

export class NavControls extends Component<NavControlsProps> {
    shouldComponentUpdate(nextProps: Readonly<NavControlsProps>) {
        const {history, reloading} = this.props;

        return (
            reloading !== nextProps.reloading || !isEqual(history, nextProps.history)
        );
    }

    checkPathForBack = (path: string) => {
        if (!path) {
            return false;
        }

        return !['/'].includes(path);
    };

    goBack = () => {
        const {history} = this.props;

        history.goBack();
    };

    goForward = () => {
        const {history} = this.props;

        history.goForward();
    };

    refresh = () => {
        const {reloadFn} = this.props;

        reloadFn();
    };

    render() {
        const {history, reloading} = this.props;

        // @ts-ignore this is for making ide happy. code compiles without error.
        const {entries, index} = history;

        let canGoBack = false;
        if (entries[index - 1]) {
            canGoBack = this.checkPathForBack(entries[index - 1].pathname);
        }

        const canGoForward = !!entries[index + 1];

        const backClassName = `back ${!canGoBack ? 'disabled' : ''}`;
        const forwardClassName = `forward ${!canGoForward ? 'disabled' : ''}`;
        const reloadClassName = `reload ${reloading ? 'disabled' : ''}`;

        return (<div className="nav-controls">
            <div className={backClassName} onClick={() => this.goBack()}>{arrowLeftSvg}</div>
            <div className={forwardClassName} onClick={() => this.goForward()}>{arrowRightSvg}</div>
            <div className={reloadClassName} onClick={() => this.refresh()}>{refreshSvg}</div>
        </div>)
    }
}


interface Props {
    history: History;
    location: Location;
    global: Global;
    trendingTags: TrendingTags;
    users: User[];
    activeUser: ActiveUser | null;
    ui: UI;
    notifications: Notifications;
    fetchTrendingTags: () => void;
    toggleTheme: () => void;
    addUser: (user: User) => void;
    setActiveUser: (username: string | null) => void;
    updateActiveUser: (data?: Account) => void;
    addAccount: (data: Account) => void;
    deleteUser: (username: string) => void;
    fetchNotifications: (since: string | null) => void;
    fetchUnreadNotificationCount: () => void;
    setNotificationsFilter: (filter: NotificationFilter | null) => void;
    markNotifications: (id: string | null) => void;
    toggleUIProp: (what: ToggleType) => void;
    reloadFn: () => any,
    reloading: boolean,
}

interface State {
    floating: boolean,
}

export class NavBar extends Component<Props, State> {
    state: State = {
        floating: false
    }

    timer: any = null;
    nav = React.createRef<HTMLDivElement>();

    componentDidMount() {
        this.detect();
        window.addEventListener("scroll", this.scrollChanged);
        window.addEventListener("resize", this.scrollChanged);

        // fetch trending tags for global usage
        const {fetchTrendingTags} = this.props;
        fetchTrendingTags();
    }

    componentWillUnmount() {
        window.removeEventListener("scroll", this.scrollChanged);
        window.removeEventListener("resize", this.scrollChanged);
    }

    shouldComponentUpdate(nextProps: Readonly<Props>, nextState: Readonly<State>): boolean {
        return !isEqual(this.props.global, nextProps.global)
            || !isEqual(this.props.trendingTags, nextProps.trendingTags)
            || !isEqual(this.props.users, nextProps.users)
            || !isEqual(this.props.activeUser, nextProps.activeUser)
            || !isEqual(this.props.ui, nextProps.ui)
            || !isEqual(this.props.notifications, nextProps.notifications)
            || !isEqual(this.props.reloading, nextProps.reloading)
            || !isEqual(this.state, nextState)
    }

    scrollChanged = () => {
        clearTimeout(this.timer);
        this.timer = setTimeout(this.detect, 100);
    }

    detect = () => {
        const nav = this.nav.current;
        if (!nav) return;

        const limit = nav.clientHeight * 2;
        const floating = window.scrollY >= limit;

        if (floating) {
            nav.classList.add("can-float");
        } else {
            nav.classList.remove("can-float");
        }

        this.setState({floating});
    }

    changeTheme = () => {
        this.props.toggleTheme();
    };

    render() {
        const {global, activeUser, history, location, ui} = this.props;
        const themeText = global.theme == Theme.day ? _t("navbar.night-theme") : _t("navbar.day-theme");
        const logoHref = activeUser ? `/@${activeUser.username}/feed` : '/';

        const {floating} = this.state;

        return (
            <>
                {floating && (<div className="nav-bar-electron-rep"/>)}
                <div ref={this.nav} className={_c(`nav-bar-electron`)}>
                    <div className="nav-bar-inner">
                        <div className="brand">
                            <Link to={logoHref}>
                                <img src={logo} className="logo" alt="Logo"/>
                            </Link>
                        </div>

                        <div className="nav-controls">
                            <NavControls
                                history={history}
                                reloading={this.props.reloading}
                                reloadFn={this.props.reloadFn}/>
                        </div>

                        <div className="address-bar">
                            <AddressBar history={history} location={location}/>
                        </div>

                        <ToolTip content={themeText}>
                            <div className="switch-theme" onClick={this.changeTheme}>
                                {brightnessSvg}
                            </div>
                        </ToolTip>

                        {!activeUser && (
                            <div className="login-required">
                                <Button variant="outline-primary" onClick={() => {
                                    const {toggleUIProp} = this.props;
                                    toggleUIProp('login');
                                }}>{_t("g.login")}</Button>

                                <Button variant="primary" onClick={() => {
                                    const {toggleUIProp} = this.props;
                                    toggleUIProp('signUp');
                                }}>{_t("g.signup")}</Button>
                            </div>
                        )}

                        <div className="submit-post">
                            <ToolTip content={_t("navbar.post")}>
                                <Link className="btn btn-outline-primary" to="/submit">
                                    {pencilOutlineSvg}
                                </Link>
                            </ToolTip>
                        </div>

                        {activeUser && <UserNav {...this.props} activeUser={activeUser}/>}
                    </div>

                    {ui.login && <Login {...this.props} />}
                    {ui.signUp && <SignUp {...this.props} />}
                    <NotificationHandler {...this.props} />
                </div>
            </>
        )
    }
}

export default (p: Props) => {
    const props: Props = {
        history: p.history,
        location: p.location,
        global: p.global,
        trendingTags: p.trendingTags,
        users: p.users,
        activeUser: p.activeUser,
        ui: p.ui,
        notifications: p.notifications,
        fetchTrendingTags: p.fetchTrendingTags,
        toggleTheme: p.toggleTheme,
        addUser: p.addUser,
        setActiveUser: p.setActiveUser,
        updateActiveUser: p.updateActiveUser,
        addAccount: p.addAccount,
        deleteUser: p.deleteUser,
        fetchNotifications: p.fetchNotifications,
        fetchUnreadNotificationCount: p.fetchUnreadNotificationCount,
        setNotificationsFilter: p.setNotificationsFilter,
        markNotifications: p.markNotifications,
        toggleUIProp: p.toggleUIProp,
        reloadFn: p.reloadFn,
        reloading: p.reloading,
    }

    return <NavBar {...props} />;
}

