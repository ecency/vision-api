import React, {Component} from "react";
import {History, Location} from "history";
import {Link} from "react-router-dom";

import {Global} from "../../store/global/types";
import {User} from "../../store/users/types";
import {Account} from "../../store/accounts/types";
import {ActiveUser} from "../../store/active-user/types";
import {ToggleType, UI} from "../../store/ui/types";
import {NotificationFilter, Notifications} from "../../store/notifications/types";
import {DynamicProps} from "../../store/dynamic-props/types";

import ToolTip from "../tooltip";
import UserAvatar from "../user-avatar";
import DropDown from "../dropdown";
import UserNotifications from "../notifications";
import Gallery from "../gallery";
import Drafts from "../drafts";
import Bookmarks from "../bookmarks";
import Schedules from "../schedules";

import {_t} from "../../i18n";

import HiveWallet from "../../helper/hive-wallet";

import {creditCardSvg, gifCardSvg, bellSvg, bellOffSvg, chevronUpSvg} from "../../img/svg";
import {vpMana} from "../../api/hive";

class WalletBadge extends Component<{
    activeUser: ActiveUser;
    dynamicProps: DynamicProps;
}> {
    render() {
        const {activeUser, dynamicProps} = this.props;

        let hasUnclaimedRewards = false;
        const {data: account} = activeUser;

        if (account.__loaded) {
            hasUnclaimedRewards = new HiveWallet(account, dynamicProps).hasUnclaimedRewards;
        }

        return <>
            <ToolTip content={hasUnclaimedRewards ? _t("user-nav.unclaimed-reward-notice") : _t("user-nav.wallet")}>
                <Link to={`/@${activeUser.username}/wallet`} className="user-wallet">
                    {hasUnclaimedRewards && <span className="reward-badge"/>}
                    {creditCardSvg}
                </Link>
            </ToolTip>
        </>
    }
}


class PointsBadge extends Component<{ activeUser: ActiveUser }> {
    render() {
        const {activeUser} = this.props;

        let hasUnclaimedPoints = activeUser.points.uPoints !== "0.000";

        return <>
            <ToolTip content={hasUnclaimedPoints ? _t("user-nav.unclaimed-points-notice") : _t("user-nav.points")}>
                <Link to={`/@${activeUser.username}/points`} className="user-points">
                    {hasUnclaimedPoints && <span className="reward-badge"/>}
                    {gifCardSvg}
                </Link>
            </ToolTip>
        </>
    }
}

interface Props {
    global: Global;
    dynamicProps: DynamicProps;
    history: History;
    location: Location;
    users: User[];
    ui: UI;
    activeUser: ActiveUser;
    notifications: Notifications;
    setActiveUser: (username: string | null) => void;
    updateActiveUser: (data?: Account) => void;
    deleteUser: (username: string) => void;
    addAccount: (data: Account) => void;
    fetchNotifications: (since: string | null) => void;
    fetchUnreadNotificationCount: () => void;
    setNotificationsFilter: (filter: NotificationFilter | null) => void;
    markNotifications: (id: string | null) => void;
    toggleUIProp: (what: ToggleType) => void;
    muteNotifications: () => void;
    unMuteNotifications: () => void;
}

interface State {
    gallery: boolean,
    drafts: boolean,
    bookmarks: boolean,
    schedules: boolean,
}

export default class UserNav extends Component<Props, State> {
    state: State = {
        gallery: false,
        drafts: false,
        bookmarks: false,
        schedules: false
    }

    toggleLogin = () => {
        const {toggleUIProp} = this.props;
        toggleUIProp('login');
    };

    toggleDrafts = () => {
        const {drafts} = this.state;
        this.setState({drafts: !drafts});
    }

    toggleGallery = () => {
        const {gallery} = this.state;
        this.setState({gallery: !gallery});
    }

    toggleBookmarks = () => {
        const {bookmarks} = this.state;
        this.setState({bookmarks: !bookmarks});
    }

    toggleSchedules = () => {
        const {schedules} = this.state;
        this.setState({schedules: !schedules});
    }

    toggleNotifications = () => {
        const {toggleUIProp} = this.props;
        toggleUIProp('notifications');
    }

    render() {
        const {gallery, drafts, bookmarks, schedules} = this.state;
        const {activeUser, ui, notifications, global, dynamicProps} = this.props;
        const {unread} = notifications;

        const vPower = activeUser.data.__loaded ? vpMana(activeUser.data) : null;

        const dropDownConfig = {
            history: this.props.history,
            label: UserAvatar({...this.props, username: activeUser.username, size: "medium"}),
            items: [
                {
                    label: _t('user-nav.profile'),
                    href: `/@${activeUser.username}`,
                },
                {
                    label: _t('user-nav.drafts'),
                    onClick: this.toggleDrafts,
                },
                {
                    label: _t('user-nav.gallery'),
                    onClick: this.toggleGallery,
                },
                {
                    label: _t('user-nav.bookmarks'),
                    onClick: this.toggleBookmarks,
                },
                {
                    label: _t('user-nav.schedules'),
                    onClick: this.toggleSchedules,
                },
                {
                    label: _t('g.login-as'),
                    onClick: this.toggleLogin,
                },
                {
                    label: _t('user-nav.logout'),
                    onClick: () => {
                        const {setActiveUser} = this.props;
                        setActiveUser(null);
                    },
                },
            ],
        };

        return (
            <>
                <div className="user-nav">
                    <PointsBadge activeUser={activeUser}/>
                    <WalletBadge activeUser={activeUser} dynamicProps={dynamicProps}/>
                    <ToolTip content={_t("user-nav.notifications")}>
                        <span className="notifications" onClick={this.toggleNotifications}>
                             {unread > 0 && (
                                 <span className="notifications-badge notranslate">
                                     {unread.toString().length < 3 ? unread : '...'}
                                 </span>
                             )}
                            {global.notifications ? bellSvg : bellOffSvg}
                        </span>
                    </ToolTip>
                    <DropDown
                        {...dropDownConfig}
                        float="right"
                        header={`@${activeUser.username}`}
                        extraElem={vPower ? <div className="drop-down-menu-vp">
                            {chevronUpSvg} {_t("user-nav.vote-power", {n: vPower.toFixed(2)})}
                        </div> : undefined}
                    />
                </div>
                {ui.notifications && <UserNotifications {...this.props} />}
                {gallery && <Gallery {...this.props} onHide={this.toggleGallery}/>}
                {drafts && <Drafts {...this.props} onHide={this.toggleDrafts}/>}
                {bookmarks && <Bookmarks {...this.props} onHide={this.toggleBookmarks}/>}
                {schedules && <Schedules {...this.props} onHide={this.toggleSchedules}/>}
            </>
        );
    }
}
